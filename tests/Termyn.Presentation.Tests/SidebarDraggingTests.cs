using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Dragging in the sidebar: projects and sections reordered among their own, and tasks dragged in
/// from the outline onto the project or section they should go to.
/// </summary>
/// <remarks>
/// Whether the account takes a drop is <c>StructureDropTests</c>. These are the presenter's half:
/// which rows drag at all, which rows a drop can be by, and what a drop leaves in the sidebar.
/// </remarks>
public class SidebarDraggingTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- Which rows drag -----------------------------------------------------------------------

    [Fact]
    public void A_project_or_section_drags_from_its_own_place_in_the_tree()
    {
        var presenter = NewPresenter();

        Assert.True(presenter.CanDragInSidebar(Row(presenter, "project:p")));
        Assert.True(presenter.CanDragInSidebar(Row(presenter, "section:s1")));
    }

    [Fact]
    public void A_favourite_copy_the_inbox_and_every_other_row_stay_put()
    {
        // The Favourites copy would have to reorder the Projects list somewhere else on screen,
        // and Todoist keeps the Inbox at the top whatever its order says.
        var presenter = NewPresenter();

        Assert.False(presenter.CanDragInSidebar(Row(presenter, SidebarKeys.Favourite(SidebarKind.Project, "p"))));
        Assert.False(presenter.CanDragInSidebar(Row(presenter, "project:in")));
        Assert.False(presenter.CanDragInSidebar(Row(presenter, "Today")));
        Assert.False(presenter.CanDragInSidebar(Row(presenter, "label:followup")));
        Assert.False(presenter.CanDragInSidebar(Row(presenter, "filter:f1")));
    }

    // ---- Where a dragged row can go ------------------------------------------------------------

    [Fact]
    public void A_project_can_be_put_down_beside_another_project_in_the_tree()
    {
        var presenter = NewPresenter();

        Assert.True(presenter.CanDropInSidebar(Row(presenter, "project:q"), Row(presenter, "project:p"), after: false));
    }

    [Fact]
    public void A_dragged_row_is_not_put_down_beside_a_favourite_copy_or_a_row_of_another_kind()
    {
        var presenter = NewPresenter();
        var home = Row(presenter, "project:q");

        Assert.False(presenter.CanDropInSidebar(home, Row(presenter, SidebarKeys.Favourite(SidebarKind.Project, "p")), after: false));
        Assert.False(presenter.CanDropInSidebar(home, Row(presenter, "section:s1"), after: false));
        Assert.False(presenter.CanDropInSidebar(Row(presenter, "section:s2"), Row(presenter, "project:p"), after: true));
    }

    [Fact]
    public void A_project_put_down_beside_another_is_reordered_in_the_sidebar()
    {
        var presenter = NewPresenter();

        Assert.True(presenter.DropInSidebar(Row(presenter, "project:q"), Row(presenter, "project:p"), after: false));

        var order = presenter.Sidebar
            .Where(n => n.Kind == SidebarKind.Project && n.Key == SidebarKeys.For(n.Kind, n.Id) && n.Id != "in")
            .Select(n => n.Label);
        Assert.Equal(["Home", "Work"], order);
        Assert.Equal("Moved the project “Home”", presenter.History.Recent()[0].Said);
    }

    [Fact]
    public void A_section_put_down_beside_another_is_reordered_in_the_sidebar()
    {
        var presenter = NewPresenter();

        Assert.True(presenter.DropInSidebar(Row(presenter, "section:s2"), Row(presenter, "section:s1"), after: false));

        var order = presenter.Sidebar.Where(n => n.Kind == SidebarKind.Section).Select(n => n.Label);
        Assert.Equal(["Later", "Next"], order);
        Assert.Equal("Moved the section “Later”", presenter.History.Recent()[0].Said);
    }

    [Fact]
    public void A_row_dropped_where_it_cannot_go_changes_nothing()
    {
        var presenter = NewPresenter();

        Assert.False(presenter.DropInSidebar(Row(presenter, "project:q"), Row(presenter, "section:s1"), after: false));
        Assert.False(presenter.DropInSidebar(Row(presenter, "project:in"), Row(presenter, "project:p"), after: false));

        // The engine would take this one, since it's the same project: it's the row that's wrong.
        Assert.False(presenter.DropInSidebar(Row(presenter, "project:q"), Row(presenter, SidebarKeys.Favourite(SidebarKind.Project, "p")), after: false));
        Assert.Empty(presenter.History.Recent());
    }

    [Fact]
    public void A_project_and_a_section_sharing_an_id_are_not_taken_for_each_other()
    {
        // Todoist ids are only unique within a resource type, so a section can carry the id of a
        // project. Dropped beside that section, a project mustn't be placed beside the project; and
        // a task dropped on the project mustn't go into the section.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("projects", "q", """{"id":"q","name":"Home","child_order":2}""");
        store.PutResource("sections", "q", """{"id":"q","name":"Errands","project_id":"p","section_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        var presenter = NewPresenter(store);

        Assert.False(presenter.CanDropInSidebar(Row(presenter, "project:p"), Row(presenter, "section:q"), after: true));
        Assert.False(presenter.DropInSidebar(Row(presenter, "project:p"), Row(presenter, "section:q"), after: true));

        Assert.True(presenter.DropTaskOn("a", Row(presenter, "project:q")));
        Assert.Equal("Moved “A” to “Home”", presenter.History.Recent()[0].Said);
    }

    // ---- A task dragged in from the outline ----------------------------------------------------

    [Fact]
    public void A_project_or_section_takes_a_task_from_elsewhere()
    {
        var presenter = NewPresenter();

        Assert.True(presenter.CanDropTaskOn("a", Row(presenter, "project:q")));
        Assert.True(presenter.CanDropTaskOn("a", Row(presenter, "section:s2")));

        // The Favourites copy is the same project, so it takes a task the same way.
        Assert.True(presenter.CanDropTaskOn("b", Row(presenter, SidebarKeys.Favourite(SidebarKind.Project, "p"))));
    }

    [Fact]
    public void A_task_is_not_offered_where_it_already_is_or_to_a_row_that_holds_no_tasks()
    {
        var presenter = NewPresenter();

        Assert.False(presenter.CanDropTaskOn("a", Row(presenter, "project:p")));
        Assert.False(presenter.CanDropTaskOn("a", Row(presenter, SidebarKeys.Favourite(SidebarKind.Project, "p"))));
        Assert.False(presenter.CanDropTaskOn("b", Row(presenter, "section:s1")));
        Assert.False(presenter.CanDropTaskOn("a", Row(presenter, "Today")));
        Assert.False(presenter.CanDropTaskOn("a", Row(presenter, "label:followup")));
    }

    [Fact]
    public void A_task_dropped_on_a_section_moves_there_and_says_where()
    {
        var presenter = NewPresenter();

        Assert.True(presenter.DropTaskOn("a", Row(presenter, "section:s2")));

        Assert.Equal("Moved “A” to “Work / Later”", presenter.History.Recent()[0].Said);
        presenter.Select(ViewSelection.OfSection("s2"));
        Assert.Contains(presenter.Rows, r => r.Id == "a");
    }

    [Fact]
    public void A_task_dropped_on_the_favourite_copy_of_a_project_moves_to_that_project()
    {
        var presenter = NewPresenter();

        Assert.True(presenter.DropTaskOn("b", Row(presenter, SidebarKeys.Favourite(SidebarKind.Project, "p"))));

        Assert.Equal("Moved “B” to “Work”", presenter.History.Recent()[0].Said);
    }

    [Fact]
    public async Task A_task_renamed_by_a_sync_mid_drag_is_named_when_it_lands()
    {
        // Captured offline, it goes by a name of ours until a sync learns the server's, and that can
        // land with the drag under way. The drag still holds the old name.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("projects", "q", """{"id":"q","name":"Home","child_order":2}""");
        var api = new FakeApi();
        var presenter = NewPresenter(store, api);

        await presenter.CaptureAsync("Fresh", TestContext.Current.CancellationToken);
        var ours = presenter.Rows.Single(r => r.Content == "Fresh").Id;

        api.Next = FakeApi.Naming(new Dictionary<string, string> { ["item_add"] = "fresh" });
        await presenter.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Contains(presenter.Rows, r => r.Id == "fresh");

        Assert.True(presenter.DropTaskOn(ours, Row(presenter, "project:q")));
        Assert.Equal("Moved “Fresh” to “Home”", presenter.History.Recent()[0].Said);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static SidebarNode Row(MainPresenter presenter, string key) => presenter.Sidebar.Single(n => n.Key == key);

    /// <summary>
    /// The Inbox; "Work", a favourite, with two sections; "Home"; a label and a filter. "A" sits at
    /// the top level of "Work" and "B" in its section "s1", with "Work" open.
    /// </summary>
    private static MainPresenter NewPresenter()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "in", """{"id":"in","name":"Inbox","is_inbox_project":true,"child_order":0}""");
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1,"is_favorite":true}""");
        store.PutResource("projects", "q", """{"id":"q","name":"Home","child_order":2}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Next","project_id":"p","section_order":1}""");
        store.PutResource("sections", "s2", """{"id":"s2","name":"Later","project_id":"p","section_order":2}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup"}""");
        store.PutResource("filters", "f1", """{"id":"f1","name":"Soon","query":"today"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","section_id":"s1","child_order":2}""");
        return NewPresenter(store);
    }

    /// <summary>A presenter over the given account, with project "p" open.</summary>
    private static MainPresenter NewPresenter(InMemorySnapshotStore store, FakeApi? api = null)
    {
        var engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.OfProject("p"));
        return presenter;
    }
}
