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

        Assert.Contains(palette, e => e is { Kind: PaletteKind.Action, Command: AppCommand.NewProject });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Action, Command: AppCommand.SyncNow });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.SmartView, Label: "Today" });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Project, Label: "Work" });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Section, Label: "Later" });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Label, Label: "urgent" });
        Assert.Contains(palette, e => e is { Kind: PaletteKind.Filter, Label: "Hot" });

        // Group headers are not places you can go.
        Assert.DoesNotContain(palette, e => e.Label is "Projects" or "Labels" or "Favourites");
    }

    [Fact]
    public async Task Every_action_the_palette_offers_is_listed_once()
    {
        // Spelt out rather than derived: the palette offers some of the app's commands, not all of
        // them, so the list is the specification. An action added twice, or dropped, fails here.
        var presenter = await Loaded();

        var actions = presenter.Palette("").Where(e => e.Kind == PaletteKind.Action).ToList();

        Assert.Equal(
            [
                AppCommand.NewTask,
                AppCommand.NewProject,
                AppCommand.NewSection,
                AppCommand.SyncNow,
                AppCommand.ToggleCompleted,
                AppCommand.Undo,
                AppCommand.Settings,
                AppCommand.CheckForUpdates,
                AppCommand.About,
            ],
            actions.Select(e => e.Command).ToArray());
    }

    [Fact]
    public async Task The_palette_calls_an_action_what_the_menus_call_it()
    {
        // One catalogue behind all three surfaces, so renaming an action in it renames it in the
        // menu bar, the right-click menu and here — rather than in two of the three.
        var presenter = await Loaded();

        Assert.All(Actions(presenter), e => Assert.Equal(Expected(presenter, e.Command), e.Label));
    }

    [Fact]
    public async Task An_action_the_palette_marks_for_the_state_follows_it()
    {
        // The state the catalogue gives back depends on what the presenter is doing. Checking only
        // the resting state would pass just as well if the palette asked the catalogue about nobody
        // in particular — which it did once, and this is what would catch it happening again. The
        // question used to be asked of the label, before the label stopped saying it.
        var api = new FakeApi
        {
            Response = new SyncResponse { SyncToken = "s1" },
            Completed = _ => new CompletedPage([], null),
        };
        var presenter = await Loaded(api);

        Assert.False(CheckedFor(presenter, AppCommand.ToggleCompleted));

        Assert.True(await presenter.ToggleCompletedAsync());

        Assert.True(CheckedFor(presenter, AppCommand.ToggleCompleted));

        // And still whatever the catalogue would say for the state it is actually in.
        Assert.All(Actions(presenter), e => Assert.Equal(Expected(presenter, e.Command), e.Label));
    }

    private static IEnumerable<PaletteEntry> Actions(MainPresenter presenter)
        => presenter.Palette("").Where(e => e.Kind == PaletteKind.Action);

    private static string LabelOf(MainPresenter presenter, AppCommand command)
        => Actions(presenter).Single(e => e.Command == command).Label;

    private static bool CheckedFor(MainPresenter presenter, AppCommand command)
        => Actions(presenter).Single(e => e.Command == command).Checked;

    /// <summary>
    /// What the catalogue calls a command for the state the presenter is in — the same question the
    /// menus ask of it, rather than one asked about nothing in particular.
    /// </summary>
    private static string Expected(MainPresenter presenter, AppCommand command)
        => Presentation.Commands.StateOf(
            command,
            new CommandContext(ShowingCompleted: presenter.ShowingCompleted, CanUndo: presenter.CanUndo)).Label;

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
    public async Task The_completed_action_carries_whether_they_are_showing()
    {
        // The palette draws no tick of its own, so the row is handed the state to mark itself with.
        // The name stays put either way — see CommandState for why it isn't spelled into the label.
        var api = Api();
        api.Completed = _ => new CompletedPage([], null);
        var presenter = await Loaded(api);

        var before = presenter.Palette("").Single(e => e.Command == AppCommand.ToggleCompleted);
        Assert.Equal("Completed tasks", before.Label);
        Assert.False(before.Checked);

        await presenter.ToggleCompletedAsync();

        var after = presenter.Palette("").Single(e => e.Command == AppCommand.ToggleCompleted);
        Assert.Equal("Completed tasks", after.Label);
        Assert.True(after.Checked);
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
