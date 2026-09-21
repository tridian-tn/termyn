using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The views most recently opened, which the tray offers as a way back to them.
/// </summary>
/// <remarks>
/// Kept as keys and read against the sidebar when asked for, so what the list says is always what
/// the account currently holds. The rules worth holding down are that a view appears once however
/// often it's opened, that the one on screen isn't offered, and that a view the account no longer
/// has quietly drops out.
/// </remarks>
public class RecentViewsTests
{
    private static readonly DateOnly Today = new(2026, 9, 21);

    /// <param name="upTo">The highest project number to seed, for a test that needs more than three</param>
    private static (MainPresenter Presenter, InMemorySnapshotStore Store, FakeApi Api) Seeded(int upTo = 3)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Home","child_order":2}""");
        store.PutResource("projects", "p3", """{"id":"p3","name":"Admin","child_order":3}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup","item_order":1}""");

        for (var i = 4; i <= upTo; i++)
            store.PutResource("projects", $"p{i}", $$"""{"id":"p{{i}}","name":"Project {{i}}","child_order":{{i}}}""");

        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
        return (presenter, store, api);
    }

    private static string[] Offered(MainPresenter presenter) => presenter.RecentViews.Select(v => v.Label).ToArray();

    [Fact]
    public void The_views_opened_are_offered_newest_first()
    {
        var (presenter, _, _) = Seeded();

        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.Select(ViewSelection.OfProject("p2"));
        presenter.Select(ViewSelection.Of(SmartView.Today));

        // Today is where we are now, so it isn't offered; the rest are, newest first.
        Assert.Equal(["Home", "Work"], Offered(presenter));
    }

    [Fact]
    public void The_view_on_screen_is_not_offered()
    {
        // From the tray, opening the window already lands you there. An entry for it would be one
        // of five places wasted.
        var (presenter, _, _) = Seeded();

        presenter.Select(ViewSelection.OfProject("p1"));

        Assert.DoesNotContain("Work", Offered(presenter));
    }

    [Fact]
    public void A_view_opened_again_moves_up_rather_than_appearing_twice()
    {
        var (presenter, _, _) = Seeded();

        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.Select(ViewSelection.OfProject("p2"));
        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.Select(ViewSelection.Of(SmartView.Today));

        Assert.Equal(["Work", "Home"], Offered(presenter));
    }

    [Fact]
    public void No_more_than_five_are_offered()
    {
        var (presenter, _, _) = Seeded(upTo: 10);

        foreach (var i in Enumerable.Range(4, 7))
            presenter.Select(ViewSelection.OfProject($"p{i}"));

        presenter.Select(ViewSelection.Of(SmartView.Today));

        Assert.Equal(5, presenter.RecentViews.Count);
        Assert.Equal(["Project 10", "Project 9", "Project 8", "Project 7", "Project 6"], Offered(presenter));
    }

    [Fact]
    public async Task A_project_the_account_no_longer_has_drops_out()
    {
        // Deleted on the web, and the tombstone arriving on the next sync. The list is read against
        // the sidebar rather than stored as labels, so it can't offer a view that has gone.
        var (presenter, _, api) = Seeded();
        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.Select(ViewSelection.OfProject("p2"));
        presenter.Select(ViewSelection.Of(SmartView.Today));

        api.Response = new SyncResponse { SyncToken = "s1", Changes = [Json.Deleted("projects", "p1")] };
        await presenter.SyncAsync();

        Assert.Equal(["Home"], Offered(presenter));
    }

    [Fact]
    public async Task A_renamed_project_is_offered_by_the_name_it_has_now()
    {
        var (presenter, _, api) = Seeded();
        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.Select(ViewSelection.Of(SmartView.Today));

        api.Response = new SyncResponse
        {
            SyncToken = "s1",
            Changes = [Json.Change("projects", "p1", """{"id":"p1","name":"Work (2026)","child_order":1}""")],
        };
        await presenter.SyncAsync();

        Assert.Equal(["Work (2026)"], Offered(presenter));
    }

    [Fact]
    public void Labels_and_smart_views_are_offered_like_anything_else()
    {
        var (presenter, _, _) = Seeded();

        presenter.Select(ViewSelection.Of(SmartView.Upcoming));
        presenter.Select(ViewSelection.OfLabel("followup"));
        presenter.Select(ViewSelection.OfProject("p1"));

        Assert.Equal(["followup", "Upcoming"], Offered(presenter));
    }

    [Fact]
    public void A_favourited_project_is_offered_once_however_it_was_opened()
    {
        // It's two rows in the sidebar — one under Favourites, one in the tree — with a key each.
        // Offered by key alone it would be the same project twice, under the same name.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1,"is_favorite":true}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Home","child_order":2}""");

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));

        presenter.SelectByKey(SidebarKeys.Favourite(SidebarKind.Project, "p1"));
        presenter.SelectByKey(SidebarKeys.For(SidebarKind.Project, "p1"));
        presenter.Select(ViewSelection.Of(SmartView.Today));

        Assert.Equal(["Work"], Offered(presenter));
    }

    [Fact]
    public void Standing_on_one_copy_of_a_project_does_not_offer_the_other()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1,"is_favorite":true}""");

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));

        presenter.SelectByKey(SidebarKeys.For(SidebarKind.Project, "p1"));
        presenter.SelectByKey(SidebarKeys.Favourite(SidebarKind.Project, "p1"));

        // The other row opens the view already on screen, which is the thing this leaves out.
        Assert.Empty(presenter.RecentViews);
    }

    [Fact]
    public void Nothing_is_offered_before_anywhere_has_been_opened()
        => Assert.Empty(Seeded().Presenter.RecentViews);
}
