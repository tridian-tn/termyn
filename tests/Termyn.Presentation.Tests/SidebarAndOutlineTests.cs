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
            .Where(n => n.Kind is SidebarKind.Project or SidebarKind.Section)
            .Select(n => (n.Label, n.Depth))
            .ToArray();

        Assert.Equal([("Work", 1), ("Admin", 2), ("Client", 2)], structure);
    }

    [Fact]
    public void Smart_views_sit_at_the_top_level_not_under_one_another()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        var presenter = NewPresenter(store);

        // Everything that isn't a project or section belongs at depth 0, or the tree rebuild hangs
        // the projects off whichever smart view happened to come last.
        Assert.All(
            presenter.Sidebar.Where(n => n.Kind is SidebarKind.SmartView or SidebarKind.Header),
            n => Assert.Equal(0, n.Depth));

        Assert.Contains(presenter.Sidebar, n => n.Kind == SidebarKind.Header && n.Label == "Projects");
    }

    [Fact]
    public void A_favourite_is_keyed_apart_from_its_copy_in_the_tree()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Music","is_favorite":true}""");
        var presenter = NewPresenter(store);

        var music = presenter.Sidebar.Where(n => n.Id == "p1").ToList();

        // The same project listed twice, but the two rows must be distinguishable.
        Assert.Equal(2, music.Count);
        Assert.Equal(2, music.Select(n => n.Key).Distinct().Count());
    }

    [Fact]
    public void Archived_projects_and_their_tasks_are_left_out()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Live"}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Old","is_archived":true,"is_favorite":true}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Gone","project_id":"p1","is_archived":true}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Current","project_id":"p1"}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Archived away","project_id":"p2"}""");
        var presenter = All(store);

        Assert.DoesNotContain(presenter.Sidebar, n => n.Label == "Old");
        Assert.DoesNotContain(presenter.Sidebar, n => n.Label == "Gone");
        Assert.Equal(new[] { "Current" }, presenter.Rows.Select(r => r.Content).ToArray());
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
        var admin = presenter.Sidebar.First(n => n.Label == "Admin");
        var today = presenter.Sidebar.First(n => n.Label == "Today");
        var inbox = presenter.Sidebar.First(n => n.Label == "Inbox");

        Assert.Equal(3, work.Count);   // every active task in Work
        Assert.Equal(1, admin.Count);  // only the one filed in the section
        Assert.Equal(1, today.Count);  // only the one due today
        Assert.Equal(1, inbox.Count);  // the task with no project of its own
    }

    // ---- Selection -----------------------------------------------------------------------------

    [Fact]
    public void Selecting_a_row_republishes_a_sidebar_equal_to_the_last_one()
    {
        // The tree is rebuilt only when the rows it shows have changed, because a rebuild drops the
        // scroll to the top. Nothing in the sidebar depends on the selection, so opening a view has
        // to leave the rows equal — a count or a label that started following the selection would
        // put the jump back without touching the view at all.
        var presenter = NewPresenter(Seeded());

        // Copied, so the rows are compared whether the presenter hands back a fresh list or the
        // one it already had. Which of those it does is its own business — reusing the list would
        // only mean the view could tell even more cheaply that nothing had moved.
        var before = presenter.Sidebar.ToList();

        Assert.True(presenter.SelectByKey(SidebarKeys.For(SidebarKind.Project, "p1")));

        Assert.Equal("p1", presenter.Selection.ProjectId); // the view really did move
        Assert.Equal(before, presenter.Sidebar);
    }

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

    [Fact]
    public async Task Capturing_with_a_project_named_ignores_the_selected_section()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "pa", """{"id":"pa","name":"Alpha"}""");
        store.PutResource("projects", "pb", """{"id":"pb","name":"Beta"}""");
        store.PutResource("sections", "sa", """{"id":"sa","name":"Notes","project_id":"pa"}""");
        var presenter = NewPresenter(store);
        presenter.Select(ViewSelection.OfSection("sa"));

        await presenter.CaptureAsync("Task #Beta");

        // Beta's task must not be filed into Alpha's section — that pair isn't a real place, and
        // the server would reject it.
        presenter.Select(ViewSelection.Of(SmartView.All));
        Assert.Equal("Beta", presenter.Rows.Single(r => r.Content == "Task").Project);
    }

    [Fact]
    public void Search_covers_every_loaded_task_not_just_the_current_view()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"needle today","due":{"date":"2026-07-31"}}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"needle someday"}""");
        var presenter = NewPresenter(store); // lands on Today, so only i1 is in view

        Assert.Single(presenter.Rows);

        presenter.Search("needle");

        Assert.Equal(2, presenter.Rows.Count);
    }

    [Fact]
    public void Deleting_a_project_while_one_of_its_sections_is_selected_falls_back_to_Today()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfSection("s1"));

        presenter.DeleteProject("p1"); // s1 belongs to p1 and goes with it

        Assert.Equal(SmartView.Today, presenter.Selection.View);
    }

    [Fact]
    public void Deleting_a_parent_project_while_a_sub_project_is_selected_falls_back_to_Today()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Parent"}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Child","parent_id":"p1"}""");
        var presenter = NewPresenter(store);
        presenter.Select(ViewSelection.OfProject("p2"));

        presenter.DeleteProject("p1");

        Assert.Equal(SmartView.Today, presenter.Selection.View);
        Assert.DoesNotContain(presenter.Sidebar, n => n.Label == "Child");
    }

    [Fact]
    public void Sections_are_listed_in_the_order_the_server_gives_them()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Zebra","project_id":"p1","section_order":1}""");
        store.PutResource("sections", "s2", """{"id":"s2","name":"Alpha","project_id":"p1","section_order":2}""");
        var presenter = NewPresenter(store);

        var sections = presenter.Sidebar.Where(n => n.Kind == SidebarKind.Section).Select(n => n.Label);

        // Not alphabetical: the user's own arrangement.
        Assert.Equal(new[] { "Zebra", "Alpha" }, sections.ToArray());
    }

    [Fact]
    public void A_parent_cycle_in_the_data_does_not_hang_the_outline()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","parent_id":"b"}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","parent_id":"a"}""");
        store.PutResource("projects", "p1", """{"id":"p1","name":"One","parent_id":"p2"}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Two","parent_id":"p1"}""");

        // Would otherwise recurse until the stack goes — and a stack overflow can't be caught.
        var presenter = All(store);

        Assert.NotNull(presenter.Rows);
        Assert.NotNull(presenter.Sidebar);
    }

    [Fact]
    public void A_resource_with_no_id_is_ignored_rather_than_recursing_forever()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "noid", """{"content":"No id at all","project_id":"p"}""");
        store.PutResource("projects", "noid", """{"name":"No id either"}""");
        store.PutResource("items", "ok", """{"id":"ok","content":"Fine","project_id":"p"}""");

        var presenter = All(store);

        Assert.Equal(new[] { "Fine" }, presenter.Rows.Select(r => r.Content).ToArray());
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
