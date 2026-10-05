using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The view a restart opens on: the one it was left on, or Today when that can't be had.
/// </summary>
/// <remarks>
/// The cache is all there is to check a saved view against as the window opens, and it's only as
/// fresh as the last sync before the restart. So the view is opened from the cache, and checked
/// again once the first sync is back.
/// </remarks>
public class RestoredViewTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static string Label(string name) => SidebarKeys.For(SidebarKind.Label, name);

    private static string Project(string id) => SidebarKeys.For(SidebarKind.Project, id);

    private static (MainPresenter Presenter, FakeApi Api) Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1,"is_favorite":true}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup","item_order":1}""");

        var api = new FakeApi();
        return (Started(store, api), api);
    }

    /// <summary>A presenter over a cache, as a start makes one.</summary>
    private static MainPresenter Started(InMemorySnapshotStore store, FakeApi api)
    {
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        return new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
    }

    [Fact]
    public void The_saved_view_is_the_one_that_opens()
    {
        var (presenter, _) = Seeded();

        presenter.RestoreView(Label("followup"));

        Assert.Equal(ViewSelection.OfLabel("followup"), presenter.Selection);
        Assert.Equal(Label("followup"), presenter.SelectedKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("label:gone")]
    [InlineData("project:p9")]
    [InlineData("header:Projects")]
    public void With_no_view_to_go_back_to_it_opens_on_Today(string? saved)
    {
        var (presenter, _) = Seeded();

        presenter.RestoreView(saved);

        Assert.Equal(ViewSelection.Default, presenter.Selection);
        Assert.Equal(ViewSelection.Default.Key, presenter.SelectedKey);
    }

    [Fact]
    public async Task A_view_the_first_sync_finds_gone_gives_way_to_Today()
    {
        // Deleted on the phone while the window was closed. The cache still had it, so it opened,
        // and the sync that followed brought the tombstone.
        var (presenter, api) = Seeded();
        presenter.RestoreView(Label("followup"));

        api.Response = new SyncResponse { SyncToken = "s1", Changes = [Json.Deleted("labels", "l1")] };
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.Default, presenter.Selection);
        Assert.Equal(ViewSelection.Default.Key, presenter.SelectedKey);
    }

    [Fact]
    public async Task A_view_the_first_sync_leaves_alone_stays_open()
    {
        var (presenter, api) = Seeded();
        presenter.RestoreView(Project("p1"));

        api.Response = new SyncResponse { SyncToken = "s1" };
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.OfProject("p1"), presenter.Selection);
        Assert.Equal(Project("p1"), presenter.SelectedKey);
    }

    [Fact]
    public async Task A_favourite_unstarred_since_stays_open_as_the_project_it_still_is()
    {
        // Its row under Favourites has gone, but the project hasn't, and it's still there to show.
        var (presenter, api) = Seeded();
        presenter.RestoreView(SidebarKeys.Favourite(SidebarKind.Project, "p1"));

        api.Response = new SyncResponse
        {
            SyncToken = "s1",
            Changes = [Json.Change("projects", "p1", """{"id":"p1","name":"Work","child_order":1,"is_favorite":false}""")],
        };
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.OfProject("p1"), presenter.Selection);
        Assert.Equal(Project("p1"), presenter.SelectedKey);
    }

    [Fact]
    public async Task Starting_offline_settles_it_on_the_first_sync_that_gets_through()
    {
        // Nothing has said the label has gone until a sync comes back, so it stays until one does.
        var (presenter, api) = Seeded();
        presenter.RestoreView(Label("followup"));

        api.Throw = new TodoistNetworkException("offline");
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.OfLabel("followup"), presenter.Selection);

        api.Throw = null;
        api.Response = new SyncResponse { SyncToken = "s1", Changes = [Json.Deleted("labels", "l1")] };
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.Default, presenter.Selection);
    }

    [Fact]
    public async Task A_view_opened_since_the_restart_is_checked_the_same_way()
    {
        // It came out of the same stale cache, and a view of a label that's gone is no more use
        // for having been clicked on.
        var (presenter, api) = Seeded();
        presenter.RestoreView(Project("p1"));
        presenter.Select(ViewSelection.OfLabel("followup"));

        api.Response = new SyncResponse { SyncToken = "s1", Changes = [Json.Deleted("labels", "l1")] };
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.Default, presenter.Selection);
    }

    [Fact]
    public async Task A_project_the_server_names_in_the_first_sync_stays_open()
    {
        // Made offline before the restart, so it was saved under an id of our own and the first
        // sync is the one that sends it. Renamed rather than gone.
        var store = new InMemorySnapshotStore();
        var api = new FakeApi();

        var before = Started(store, api);
        before.AddProject("Errands");
        var made = before.Sidebar.First(n => n.Kind == SidebarKind.Project && n.Label == "Errands").Key;

        var presenter = Started(store, api);
        presenter.RestoreView(made);

        api.Next = FakeApi.Naming(
            new Dictionary<string, string> { ["project_add"] = "p2" },
            Json.Change("projects", "p2", """{"id":"p2","name":"Errands","child_order":1}"""));
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.OfProject("p2"), presenter.Selection);
        Assert.Equal(Project("p2"), presenter.SelectedKey);
    }
}
