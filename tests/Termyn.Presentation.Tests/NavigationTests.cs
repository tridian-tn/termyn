using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Moving between views. These used to live in the window, where a saved selection was highlighted
/// without ever being opened, and stepping onto a favourite jumped to its copy in the tree instead.
/// </summary>
public class NavigationTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public async Task A_remembered_selection_opens_the_view_it_names()
    {
        var presenter = await Loaded();

        Assert.True(presenter.SelectByKey(SidebarKeys.For(SidebarKind.Project, "p1")));

        Assert.Equal("p1", presenter.Selection.ProjectId);
        Assert.Equal(["w1"], presenter.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task A_remembered_selection_that_has_gone_is_refused_rather_than_guessed_at()
    {
        var presenter = await Loaded();
        presenter.Select(ViewSelection.Of(SmartView.All));

        Assert.False(presenter.SelectByKey(SidebarKeys.For(SidebarKind.Project, "deleted-elsewhere")));

        // Left where it was, rather than falling back to somewhere the user didn't ask for.
        Assert.Null(presenter.Selection.ProjectId);
    }

    [Fact]
    public async Task A_group_label_is_not_somewhere_you_can_go()
    {
        var presenter = await Loaded();

        Assert.False(presenter.SelectByKey(SidebarKeys.For(SidebarKind.Header, "Projects")));
    }

    [Fact]
    public async Task Stepping_down_walks_the_sidebar_in_order_and_skips_the_group_labels()
    {
        var presenter = await Loaded();
        presenter.SelectByKey(SidebarKeys.For(SidebarKind.SmartView, nameof(SmartView.Today)));

        var visited = Walk(presenter);

        Assert.DoesNotContain(visited, key => key.StartsWith("header:", StringComparison.Ordinal));
        Assert.Contains(SidebarKeys.For(SidebarKind.Project, "p1"), visited);
        Assert.Contains(SidebarKeys.For(SidebarKind.Label, "urgent"), visited);
    }

    /// <summary>
    /// Steps to the far end, collecting the rows visited. Bounded: stepping is supposed to advance
    /// monotonically, and a regression that made it oscillate would otherwise hang the test rather
    /// than fail it.
    /// </summary>
    private static List<string> Walk(MainPresenter presenter)
    {
        var visited = new List<string>();
        var limit = presenter.Sidebar.Count + 1;

        while (presenter.SelectAdjacent(1))
        {
            visited.Add(presenter.SelectedKey);
            Assert.True(visited.Count <= limit, $"stepping did not terminate after {visited.Count} moves");
        }

        return visited;
    }

    [Fact]
    public async Task Stepping_reaches_the_favourites_group_rather_than_jumping_past_it()
    {
        // A favourited project is two rows — one under Favourites, one in the tree — and stepping by
        // selection landed on the tree copy both times, so the group could never be walked.
        var presenter = await Loaded();
        presenter.SelectByKey(SidebarKeys.For(SidebarKind.SmartView, nameof(SmartView.Today)));

        Assert.Contains(SidebarKeys.Favourite(SidebarKind.Project, "p1"), Walk(presenter));
    }

    [Fact]
    public async Task Stepping_past_either_end_stays_where_it_is()
    {
        var presenter = await Loaded();
        presenter.SelectByKey(SidebarKeys.For(SidebarKind.SmartView, nameof(SmartView.Today)));

        Assert.False(presenter.SelectAdjacent(-1));
        Assert.Equal(SidebarKeys.For(SidebarKind.SmartView, nameof(SmartView.Today)), presenter.SelectedKey);

        Walk(presenter);

        var last = presenter.SelectedKey;
        Assert.False(presenter.SelectAdjacent(1));
        Assert.Equal(last, presenter.SelectedKey);
    }

    [Fact]
    public async Task Selecting_a_view_directly_moves_the_remembered_row_with_it()
    {
        var presenter = await Loaded();

        presenter.Select(ViewSelection.OfLabel("urgent"));

        Assert.Equal(SidebarKeys.For(SidebarKind.Label, "urgent"), presenter.SelectedKey);
    }

    [Fact]
    public async Task A_row_that_disappears_hands_the_selection_back_to_where_the_outline_actually_is()
    {
        var presenter = await Loaded();
        presenter.SelectByKey(SidebarKeys.For(SidebarKind.Project, "p1"));

        presenter.DeleteProject("p1");

        Assert.Equal(presenter.Selection.Key, presenter.SelectedKey);
        Assert.DoesNotContain(presenter.Sidebar, n => n.Id == "p1");
    }

    [Fact]
    public async Task The_tray_count_is_the_number_the_sidebar_shows()
    {
        var presenter = await Loaded();

        var today = presenter.Sidebar
            .Single(n => n is { Kind: SidebarKind.SmartView, View: SmartView.Today }).Count;

        Assert.Equal(today, presenter.DueToday);
    }

    private static async Task<MainPresenter> Loaded()
    {
        var api = new FakeApi
        {
            Response = new SyncResponse
            {
                SyncToken = "s1",
                Changes =
                [
                    Json.Change("projects", "p1", """{"id":"p1","name":"Work","is_favorite":true,"child_order":1}"""),
                    Json.Change("projects", "p2", """{"id":"p2","name":"Home","child_order":2}"""),
                    Json.Change("sections", "s1", """{"id":"s1","name":"Later","project_id":"p1"}"""),
                    Json.Change("labels", "l1", """{"id":"l1","name":"urgent"}"""),
                    Json.Change("items", "w1", """{"id":"w1","content":"Work task","project_id":"p1","due":{"date":"2026-07-31"}}"""),
                    Json.Change("items", "h1", """{"id":"h1","content":"Home task","project_id":"p2","labels":["urgent"]}"""),
                ],
            },
        };

        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
        await presenter.LoadAsync();
        return presenter;
    }
}
