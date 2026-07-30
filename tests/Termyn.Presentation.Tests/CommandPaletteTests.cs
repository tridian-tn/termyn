using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

public class FuzzyTests
{
    [Theory]
    [InlineData("New project", "np")]        // initials
    [InlineData("New project", "newp")]
    [InlineData("New project", "project")]   // a whole word from the middle
    [InlineData("New project", "NEWPROJECT")]
    [InlineData("New project", "npj")]       // letters in order, gaps allowed
    public void Matches_the_letters_in_order(string candidate, string query)
        => Assert.NotNull(Fuzzy.Score(candidate, query));

    [Theory]
    [InlineData("New project", "pn")]        // out of order
    [InlineData("New project", "newz")]
    [InlineData("", "a")]
    public void Refuses_what_it_does_not_contain(string candidate, string query)
        => Assert.Null(Fuzzy.Score(candidate, query));

    [Fact]
    public void An_empty_query_matches_everything_equally()
    {
        Assert.Equal(0, Fuzzy.Score("New project", ""));
        Assert.Equal(0, Fuzzy.Score("", ""));
    }

    [Fact]
    public void Word_openings_beat_the_same_letters_buried_mid_word()
    {
        var initials = Fuzzy.Score("New project", "np")!.Value;
        var buried = Fuzzy.Score("Unplanned", "np")!.Value;

        Assert.True(initials > buried, $"initials {initials} should beat buried {buried}");
    }

    [Fact]
    public void A_run_of_letters_beats_the_same_letters_scattered()
    {
        var together = Fuzzy.Score("Sync now", "sync")!.Value;
        var scattered = Fuzzy.Score("Some yearly notice check", "sync")!.Value;

        Assert.True(together > scattered, $"contiguous {together} should beat scattered {scattered}");
    }

    [Fact]
    public void Ranking_drops_what_does_not_match_and_orders_the_rest()
    {
        PaletteEntry[] entries =
        [
            new(PaletteKind.Project, "Nap", "project"),
            new(PaletteKind.Project, "Personal", "project"), // no p after its n, so not a match at all
            new(PaletteKind.Action, "New project", "action"),
        ];

        var ranked = Fuzzy.Rank(entries, "np");

        Assert.Equal(["New project", "Nap"], ranked.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void An_empty_query_keeps_the_order_it_was_given()
    {
        PaletteEntry[] entries =
        [
            new(PaletteKind.Action, "Zebra", "action"),
            new(PaletteKind.Action, "Apple", "action"),
        ];

        Assert.Equal(["Zebra", "Apple"], Fuzzy.Rank(entries, "").Select(e => e.Label).ToArray());
    }

    [Fact]
    public void The_hint_is_searchable_but_never_outranks_a_name()
    {
        PaletteEntry[] entries =
        [
            new(PaletteKind.Project, "Admin", "project"),   // reachable by its kind
            new(PaletteKind.Project, "Projections", "project"),
        ];

        var ranked = Fuzzy.Rank(entries, "project");

        Assert.Equal(["Projections", "Admin"], ranked.Select(e => e.Label).ToArray());
    }

    [Fact]
    public void Results_are_capped()
    {
        var many = Enumerable.Range(0, 200).Select(i => new PaletteEntry(PaletteKind.Project, $"Task {i}", "project"));

        Assert.Equal(60, Fuzzy.Rank(many, "task").Count);
        Assert.Equal(60, Fuzzy.Rank(many, "").Count);
    }
}

public class CommandPaletteTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public async Task Offers_the_actions_and_every_place_the_sidebar_reaches()
    {
        var presenter = await Loaded();

        var palette = presenter.Palette("");

        Assert.Contains(palette, e => e is { Kind: PaletteKind.Action, Command: PaletteCommand.NewProject });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Action, Command: PaletteCommand.SyncNow });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.SmartView, Label: "Today" });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Project, Label: "Work" });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Section, Label: "Later" });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Label, Label: "urgent" });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Filter, Label: "Hot" });

        // Group headers are not places you can go.
        Assert.DoesNotContain(palette, e => e.Label is "Projects" or "Labels" or "Favourites");
    }

    [Fact]
    public async Task A_favourited_project_is_listed_once()
    {
        var presenter = await Loaded();

        // It appears twice in the sidebar — under Favourites and in the tree — and once here.
        Assert.Single(presenter.Palette(""), e => e is { Kind: PaletteKind.Project, Label: "Work" });
    }

    [Fact]
    public async Task Choosing_a_place_carries_the_selection_that_opens_it()
    {
        var presenter = await Loaded();

        var entry = presenter.Palette("work").First(e => e.Kind == PaletteKind.Project);
        presenter.Select(entry.Selection!);

        Assert.Equal("p1", presenter.Selection.ProjectId);
    }

    [Fact]
    public async Task A_label_is_addressed_by_name_the_way_the_sidebar_addresses_it()
    {
        var presenter = await Loaded();

        var entry = presenter.Palette("urgent").First(e => e.Kind == PaletteKind.Label);

        Assert.Equal("urgent", entry.Selection!.LabelName);
    }

    [Fact]
    public async Task The_completed_action_says_which_way_it_will_go()
    {
        var api = Api();
        api.Completed = _ => new CompletedPage([], null);
        var presenter = await Loaded(api);

        Assert.Contains(presenter.Palette(""), e => e.Label == "Show completed tasks");

        await presenter.ToggleCompletedAsync();

        Assert.Contains(presenter.Palette(""), e => e.Label == "Hide completed tasks");
    }

    [Fact]
    public async Task Typing_narrows_it()
    {
        var presenter = await Loaded();

        var palette = presenter.Palette("work");

        Assert.Contains(palette, e => e.Label == "Work");
        Assert.DoesNotContain(palette, e => e.Label == "Today");
    }

    private static async Task<MainPresenter> Loaded(FakeApi? api = null)
    {
        var engine = new SyncEngine(api ?? Api(), new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
        await presenter.LoadAsync();
        return presenter;
    }

    private static FakeApi Api() => new()
    {
        Response = new SyncResponse
        {
            SyncToken = "s1",
            Changes =
            [
                Json.Change("projects", "p1", """{"id":"p1","name":"Work","is_favorite":true}"""),
                Json.Change("sections", "s1", """{"id":"s1","name":"Later","project_id":"p1"}"""),
                Json.Change("labels", "l1", """{"id":"l1","name":"urgent"}"""),
                Json.Change("filters", "f1", """{"id":"f1","name":"Hot","query":"p1"}"""),
            ],
        },
    };
}
