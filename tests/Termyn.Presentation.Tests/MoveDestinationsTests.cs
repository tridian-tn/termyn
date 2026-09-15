using Termyn.Core.Capture;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Where a task can be moved to: which places are offered, what each is called, and which of them
/// match what's been typed.
/// </summary>
public class MoveDestinationsTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- What is offered -----------------------------------------------------------------------

    [Fact]
    public void Every_project_and_section_is_offered_in_the_sidebars_order_with_the_path_to_it()
    {
        var presenter = NewPresenter(Account());

        var offered = presenter.DestinationsFor("t1").Select(d => (d.Kind, d.Path, d.Depth)).ToArray();

        Assert.Equal(
            [
                (SidebarKind.Project, "Work", 0),
                (SidebarKind.Section, "Work / Admin", 1),
                (SidebarKind.Project, "Work / Clients", 1),
                (SidebarKind.Section, "Work / Clients / Billing", 2),
                (SidebarKind.Project, "Home", 0),
                (SidebarKind.Section, "Home / Admin", 1),
            ],
            offered);
    }

    [Fact]
    public void A_favourite_project_is_offered_once()
    {
        // It's in the sidebar twice, under Favourites and in the tree, and it's one place to go.
        var store = Account();
        store.PutResource("projects", "home", """{"id":"home","name":"Home","child_order":2,"is_favorite":true}""");
        var presenter = NewPresenter(store);

        Assert.Single(presenter.DestinationsFor("t1"), d => d.Id == "home");
    }

    [Fact]
    public void Nothing_archived_is_offered()
    {
        var store = Account();
        store.PutResource("projects", "old", """{"id":"old","name":"Old","is_archived":true}""");
        store.PutResource("sections", "gone", """{"id":"gone","name":"Gone","project_id":"work","is_archived":true}""");
        var presenter = NewPresenter(store);

        var ids = presenter.DestinationsFor("t1").Select(d => d.Id).ToList();

        Assert.DoesNotContain("old", ids);
        Assert.DoesNotContain("gone", ids);
    }

    [Fact]
    public void The_section_a_task_is_top_level_in_is_where_it_already_is()
    {
        var presenter = NewPresenter(Account());

        // In Work's Admin section. Work itself isn't "here": moving it there takes it out of the section.
        var here = Assert.Single(presenter.DestinationsFor("t1"), d => d.Here);

        Assert.Equal("Work / Admin", here.Path);
    }

    [Fact]
    public void A_task_in_no_section_is_already_in_its_project()
    {
        var presenter = NewPresenter(Account());

        var here = Assert.Single(presenter.DestinationsFor("loose"), d => d.Here);

        Assert.Equal("Home", here.Path);
    }

    [Fact]
    public void A_sub_task_is_already_nowhere()
    {
        // Every place is a move for one of those, including the section it's in: it comes out from
        // under its parent.
        var presenter = NewPresenter(Account());

        Assert.DoesNotContain(presenter.DestinationsFor("child"), d => d.Here);
    }

    // ---- What matches --------------------------------------------------------------------------

    [Fact]
    public void Nothing_typed_offers_everything_in_order()
    {
        var all = MoveDestinations.From(NewPresenter(Account()).Sidebar, null);

        Assert.Equal(all, MoveDestinations.Rank(all, "  "));
    }

    [Fact]
    public void A_few_letters_of_a_section_find_it()
    {
        var all = MoveDestinations.From(NewPresenter(Account()).Sidebar, null);

        Assert.Equal("Work / Clients / Billing", MoveDestinations.Rank(all, "bill").First().Path);
    }

    [Fact]
    public void Typing_the_path_picks_one_of_several_places_with_the_same_name()
    {
        // Two sections called Admin. The project's letters in front are what says which.
        var all = MoveDestinations.From(NewPresenter(Account()).Sidebar, null);

        Assert.Equal(["Home / Admin"], MoveDestinations.Rank(all, "hom adm").Select(d => d.Path));
    }

    [Fact]
    public void A_deeply_filed_place_isnt_outranked_for_the_length_of_its_path()
    {
        // Scored on the path alone, Work / Clients / Admin loses to a top-level Administration for
        // no better reason than being the longer string — and it's the one that was typed out in full.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "admin", """{"id":"admin","name":"Administration","child_order":1}""");
        store.PutResource("projects", "work", """{"id":"work","name":"Work","child_order":2}""");
        store.PutResource("projects", "clients", """{"id":"clients","name":"Clients","parent_id":"work","child_order":1}""");
        store.PutResource("sections", "s", """{"id":"s","name":"Admin","project_id":"clients"}""");
        var all = MoveDestinations.From(NewPresenter(store).Sidebar, null);

        Assert.Equal("Work / Clients / Admin", MoveDestinations.Rank(all, "admin").First().Path);
    }

    [Fact]
    public void Letters_nothing_is_called_offer_nothing()
    {
        var all = MoveDestinations.From(NewPresenter(Account()).Sidebar, null);

        Assert.Empty(MoveDestinations.Rank(all, "zzz"));
    }

    // ---- Moving --------------------------------------------------------------------------------

    [Fact]
    public void Moving_to_a_section_files_the_task_there()
    {
        var presenter = NewPresenter(Account());
        var billing = presenter.DestinationsFor("t1").Single(d => d.Path == "Work / Clients / Billing");

        Assert.True(presenter.MoveTo("t1", billing));

        Assert.Equal("billing", Task("t1").SectionId);
        Assert.Equal("clients", Task("t1").ProjectId);
    }

    [Fact]
    public void Moving_to_a_project_files_the_task_there_in_no_section()
    {
        var presenter = NewPresenter(Account());
        var home = presenter.DestinationsFor("t1").Single(d => d.Path == "Home");

        Assert.True(presenter.MoveTo("t1", home));

        Assert.Equal("home", Task("t1").ProjectId);
        Assert.Null(Task("t1").SectionId);
    }

    [Fact]
    public void A_move_is_written_down_with_where_it_went()
    {
        var presenter = NewPresenter(Account());
        var admin = presenter.DestinationsFor("t1").Single(d => d.Path == "Home / Admin");

        presenter.MoveTo("t1", admin);

        Assert.Equal("Moved “Write it up” to “Home / Admin”", presenter.History.Recent()[0].Said);
    }

    [Fact]
    public void Moving_to_where_it_already_is_does_nothing()
    {
        var presenter = NewPresenter(Account());
        var here = presenter.DestinationsFor("t1").Single(d => d.Here);

        Assert.False(presenter.MoveTo("t1", here));
        Assert.Empty(presenter.History.Recent());
    }

    [Fact]
    public void A_project_that_went_while_the_picker_was_open_is_not_moved_to()
    {
        // Sent anyway, the server could only refuse it — after the task had already vanished from
        // where it was into a project nothing can show.
        var presenter = NewPresenter(Account());
        var home = presenter.DestinationsFor("t1").Single(d => d.Path == "Home");

        presenter.DeleteProject("home");

        Assert.False(presenter.MoveTo("t1", home));
        Assert.Equal("work", Task("t1").ProjectId);
    }

    [Fact]
    public void A_task_the_account_holds_can_be_moved_and_one_it_doesnt_cannot()
    {
        var presenter = NewPresenter(Account());

        Assert.True(presenter.AbilitiesFor("t1").CanMoveTo);
        Assert.False(presenter.AbilitiesFor("gone").CanMoveTo);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>
    /// Two projects, one with a sub-project, and a section called Admin in each of the top two.
    /// </summary>
    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "work", """{"id":"work","name":"Work","child_order":1}""");
        store.PutResource("projects", "clients", """{"id":"clients","name":"Clients","parent_id":"work","child_order":1}""");
        store.PutResource("projects", "home", """{"id":"home","name":"Home","child_order":2}""");
        store.PutResource("sections", "wadmin", """{"id":"wadmin","name":"Admin","project_id":"work","section_order":1}""");
        store.PutResource("sections", "billing", """{"id":"billing","name":"Billing","project_id":"clients","section_order":1}""");
        store.PutResource("sections", "hadmin", """{"id":"hadmin","name":"Admin","project_id":"home","section_order":1}""");
        store.PutResource("items", "t1", """{"id":"t1","content":"Write it up","project_id":"work","section_id":"wadmin","child_order":1}""");
        store.PutResource("items", "child", """{"id":"child","content":"Draft","project_id":"work","section_id":"wadmin","parent_id":"t1","child_order":1}""");
        store.PutResource("items", "loose", """{"id":"loose","content":"Loose","project_id":"home","child_order":1}""");
        return store;
    }

    /// <summary>The engine behind the presenter last built, for reading back where a task ended up.</summary>
    private SyncEngine? _engine;

    private Termyn.Core.Model.TaskItem Task(string id) => _engine!.Snapshot().Items.Single(i => i.Id == id);

    private MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        _engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        _engine.Load();
        return new MainPresenter(_engine, new QuickAddParser(new FixedClock(Today)));
    }
}
