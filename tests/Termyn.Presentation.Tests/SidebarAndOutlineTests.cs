using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

public class SidebarAndOutlineTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- Sidebar -------------------------------------------------------------------------------

    [Fact]
    public void The_sidebar_leads_with_the_smart_views()
    {
        var presenter = NewPresenter(Seeded());

        var views = presenter.Sidebar.Where(n => n.Kind == SidebarKind.SmartView).Select(n => n.Label);

        Assert.Equal(new[] { "Today", "Upcoming", "Inbox" }, views.ToArray());
    }

    [Fact]
    public void Projects_nest_and_carry_their_sections()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Client","parent_id":"p1","child_order":1}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p1"}""");
        var presenter = NewPresenter(store);

        var structure = presenter.Sidebar
            .Where(n => n.Kind != SidebarKind.SmartView)
            .Select(n => (n.Label, n.Depth))
            .ToArray();

        Assert.Equal([("Work", 1), ("Admin", 2), ("Client", 2)], structure);
    }

    [Fact]
    public void Favourites_are_listed_before_the_project_tree()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","is_favorite":true}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Home"}""");
        var presenter = NewPresenter(store);

        var projects = presenter.Sidebar.Where(n => n.Kind == SidebarKind.Project).ToList();

        Assert.Equal("Work", projects[0].Label);
        Assert.True(projects[0].IsFavorite);
        Assert.Equal(3, projects.Count); // the favourite, then both projects in the tree
    }

    [Fact]
    public void Each_sidebar_row_carries_its_task_count()
    {
        var presenter = NewPresenter(Seeded());

        var work = presenter.Sidebar.First(n => n.Label == "Work");
        var today = presenter.Sidebar.First(n => n.Label == "Today");

        Assert.Equal(3, work.Count);   // every active task in Work
        Assert.Equal(1, today.Count);  // only the one due today
    }

    // ---- Selection -----------------------------------------------------------------------------

    [Fact]
    public void The_app_lands_on_Today()
    {
        var presenter = NewPresenter(Seeded());

        Assert.Equal(SmartView.Today, presenter.Selection.View);
        Assert.Equal(new[] { "Due today" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    [Fact]
    public void Upcoming_shows_the_week_ahead_but_not_today()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.Of(SmartView.Upcoming));

        Assert.Equal(new[] { "Due Monday" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    [Fact]
    public void Selecting_a_project_shows_only_its_tasks()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.OfProject("p1"));

        // Everything in Work, whatever its due date — including the task in its section.
        Assert.Equal(new[] { "Due Monday", "Due today", "Someday" }, presenter.Rows.Select(r => r.Content).OrderBy(c => c).ToArray());
    }

    [Fact]
    public void Selecting_a_section_shows_only_the_tasks_in_it()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.OfSection("s1"));

        Assert.Equal(new[] { "Someday" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    [Fact]
    public void Inbox_catches_tasks_that_name_no_project()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.Of(SmartView.Inbox));

        Assert.Equal(new[] { "Loose" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    // ---- Outline -------------------------------------------------------------------------------

    [Fact]
    public void Subtasks_appear_under_their_parent_with_an_indent()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Child","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"Grandchild","project_id":"p","parent_id":"b","child_order":1}""");
        var presenter = All(store);

        Assert.Equal(new[] { "Parent", "Child", "Grandchild" }, presenter.Rows.Select(r => r.Content).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, presenter.Rows.Select(r => r.Depth).ToArray());
    }

    [Fact]
    public void A_task_whose_parent_is_out_of_view_stands_on_its_own()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Child","project_id":"p","parent_id":"a","child_order":1,"due":{"date":"2026-07-31"}}""");
        var presenter = NewPresenter(store); // Today, so only the child qualifies

        var row = Assert.Single(presenter.Rows);
        Assert.Equal("Child", row.Content);
        Assert.Equal(0, row.Depth);
    }

    [Fact]
    public void Indenting_and_outdenting_move_a_task_through_the_outline()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        var presenter = All(store);

        Assert.True(presenter.Indent("b"));
        Assert.Equal(new[] { 0, 1 }, presenter.Rows.Select(r => r.Depth).ToArray());

        Assert.True(presenter.Outdent("b"));
        Assert.Equal(new[] { 0, 0 }, presenter.Rows.Select(r => r.Depth).ToArray());
    }

    [Fact]
    public void A_search_result_is_a_flat_list()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Child match","project_id":"p","parent_id":"a","child_order":1}""");
        var presenter = All(store);

        presenter.Search("match");

        var row = Assert.Single(presenter.Rows);
        Assert.Equal(0, row.Depth); // no orphaned indent without its parent on screen
    }

    // ---- Structure intents ---------------------------------------------------------------------

    [Fact]
    public void Adding_a_project_puts_it_in_the_sidebar()
    {
        var presenter = NewPresenter(new InMemorySnapshotStore());

        presenter.AddProject("Reading");

        Assert.Contains(presenter.Sidebar, n => n.Kind == SidebarKind.Project && n.Label == "Reading");
    }

    [Fact]
    public void Favouriting_a_project_lists_it_at_the_top()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        var presenter = NewPresenter(store);

        presenter.ToggleProjectFavorite("p1");

        Assert.True(presenter.Sidebar.First(n => n.Kind == SidebarKind.Project).IsFavorite);
    }

    [Fact]
    public void Deleting_the_project_being_viewed_falls_back_to_Today()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfProject("p1"));

        presenter.DeleteProject("p1");

        Assert.Equal(SmartView.Today, presenter.Selection.View);
        Assert.DoesNotContain(presenter.Sidebar, n => n.Label == "Work");
    }

    [Fact]
    public void Deleting_the_section_being_viewed_falls_back_to_Today()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfSection("s1"));

        presenter.DeleteSection("s1");

        Assert.Equal(SmartView.Today, presenter.Selection.View);
    }

    [Fact]
    public async Task Capturing_inside_a_project_files_the_task_there()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfProject("p1"));

        await presenter.CaptureAsync("Written while looking at Work");

        Assert.Contains(presenter.Rows, r => r.Content == "Written while looking at Work");
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>Work (with an Admin section) holding two tasks, one due today; plus a loose task.</summary>
    private static InMemorySnapshotStore Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p1"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Due today","project_id":"p1","child_order":1,"due":{"date":"2026-07-31"}}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Someday","project_id":"p1","section_id":"s1","child_order":2}""");
        store.PutResource("items", "i3", """{"id":"i3","content":"Due Monday","project_id":"p1","child_order":3,"due":{"date":"2026-08-03"}}""");
        store.PutResource("items", "i4", """{"id":"i4","content":"Loose","child_order":1}""");
        return store;
    }

    private static MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
    }

    private static MainPresenter All(InMemorySnapshotStore store)
    {
        var presenter = NewPresenter(store);
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
