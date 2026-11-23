using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// A project's tasks under a heading for each of its sections, and dragging them between those.
/// </summary>
public class SectionGroupingTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- What's shown --------------------------------------------------------------------------

    [Fact]
    public void Off_by_default_a_project_is_one_flat_list()
    {
        var presenter = InProject();

        Assert.False(presenter.GroupBySection);
        Assert.Equal(["Loose", "A", "Under A", "B", "X"], Shown(presenter));
        Assert.All(presenter.Rows, r => Assert.False(r.IsHeading));
    }

    [Fact]
    public void Grouped_each_section_is_headed_before_its_tasks()
    {
        var presenter = InProject(grouped: true);

        // No heading over the tasks in no section, the way Todoist shows them, and the empty
        // section headed all the same.
        Assert.Equal(["Loose", "Admin", "A", "Under A", "B", "Errands", "X", "Empty"], Shown(presenter));
        Assert.Equal(
            [false, true, false, false, false, true, false, true],
            presenter.Rows.Select(r => r.IsHeading).ToArray());
        Assert.Equal(1, presenter.Rows.Single(r => r.Id == "u").Depth);
    }

    [Fact]
    public void Sections_come_in_their_own_order_rather_than_their_tasks_order()
    {
        // "X" is first in the project's order, but its section comes second.
        var store = Store();
        store.PutResource("items", "x", """{"id":"x","content":"X","project_id":"p","section_id":"s2","child_order":-1}""");
        var presenter = InProject(store, grouped: true);

        Assert.Equal(["Loose", "Admin", "A", "Under A", "B", "Errands", "X", "Empty"], Shown(presenter));
    }

    [Fact]
    public void A_heading_is_not_a_task()
    {
        var presenter = InProject(grouped: true);

        var heading = presenter.Rows.Single(r => r.Content == "Admin");

        Assert.True(heading.IsHeading);
        Assert.Equal("s1", heading.SectionId);
        Assert.Empty(heading.Project);
        Assert.Empty(heading.Due);
        Assert.StartsWith("5 tasks", presenter.Status);
    }

    [Fact]
    public void A_sort_orders_the_tasks_within_each_section()
    {
        var store = Store();
        store.PutResource("items", "z", """{"id":"z","content":"Aardvark","project_id":"p","section_id":"s1","child_order":9}""");
        var presenter = InProject(store, grouped: true);

        presenter.SortBy(TaskColumn.Content);

        Assert.Equal(["Loose", "Admin", "A", "Under A", "Aardvark", "B", "Errands", "X", "Empty"], Shown(presenter));
    }

    [Fact]
    public void A_folded_task_hides_its_sub_tasks_under_its_heading()
    {
        var presenter = InProject(grouped: true);

        presenter.SetCollapsed("a", true);

        Assert.Equal(["Loose", "Admin", "A", "B", "Errands", "X", "Empty"], Shown(presenter));
        Assert.True(presenter.CanExpandAll);
    }

    [Theory]
    [MemberData(nameof(NotGrouped))]
    public void Only_a_project_with_sections_is_grouped(ViewSelection selection)
    {
        var presenter = InProject(grouped: true);

        presenter.Select(selection);

        Assert.False(presenter.CanGroupBySection);
        Assert.All(presenter.Rows, r => Assert.False(r.IsHeading));
    }

    public static TheoryData<ViewSelection> NotGrouped =>
    [
        ViewSelection.OfProject("q"),
        ViewSelection.OfSection("s1"),
        ViewSelection.Of(SmartView.Today),
        ViewSelection.Of(SmartView.All),
    ];

    [Fact]
    public void The_inbox_is_grouped_by_its_sections_too()
    {
        var store = Store();
        store.PutResource("sections", "si", """{"id":"si","name":"Someday","project_id":"inbox","section_order":1}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Filed","project_id":"inbox","section_id":"si","child_order":1}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Unfiled","project_id":"inbox","child_order":2}""");
        var presenter = NewPresenter(store);
        presenter.SetGroupBySection(true);

        presenter.Select(ViewSelection.Of(SmartView.Inbox));

        Assert.True(presenter.CanGroupBySection);
        Assert.Equal(["Unfiled", "Someday", "Filed"], Shown(presenter));
    }

    [Fact]
    public void A_search_drops_the_headings()
    {
        var presenter = InProject(grouped: true);

        presenter.Search("A");

        Assert.False(presenter.CanGroupBySection);
        Assert.All(presenter.Rows, r => Assert.False(r.IsHeading));
    }

    [Fact]
    public void Turning_it_off_puts_the_flat_list_back()
    {
        var presenter = InProject(grouped: true);
        var changed = 0;
        presenter.RowsChanged += () => changed++;

        Assert.True(presenter.SetGroupBySection(false));
        Assert.False(presenter.SetGroupBySection(false));

        Assert.Equal(1, changed);
        Assert.Equal(["Loose", "A", "Under A", "B", "X"], Shown(presenter));
    }

    [Fact]
    public void The_menu_entry_is_ticked_for_the_setting_and_lit_only_where_it_shows()
    {
        var on = Commands.StateOf(AppCommand.GroupBySection, new CommandContext(GroupingBySection: true, CanGroupBySection: true));
        var elsewhere = Commands.StateOf(AppCommand.GroupBySection, new CommandContext(GroupingBySection: true));
        var off = Commands.StateOf(AppCommand.GroupBySection, new CommandContext(CanGroupBySection: true));

        Assert.Equal("Group by section", on.Label);
        Assert.True(on.Enabled);
        Assert.True(on.Checked);
        Assert.False(elsewhere.Enabled);
        Assert.True(elsewhere.Checked);
        Assert.True(off.Enabled);
        Assert.False(off.Checked);
    }

    [Fact]
    public void The_palette_offers_it_with_its_tick()
    {
        var presenter = InProject(grouped: true);

        var entry = presenter.Palette("group by").Single(e => e.Command == AppCommand.GroupBySection);

        Assert.True(entry.Checked);
    }

    // ---- Dragging between sections -------------------------------------------------------------

    [Fact]
    public void A_task_dropped_beside_one_in_another_section_moves_into_it()
    {
        var presenter = InProject(grouped: true);

        Assert.True(Dropped(presenter, "b", Over(presenter, "x", DropZone.After)));

        Assert.Equal(["Loose", "Admin", "A", "Under A", "Errands", "X", "B", "Empty"], Shown(presenter));
        Assert.Equal("s2", presenter.Rows.Single(r => r.Id == "b").SectionId);
    }

    [Fact]
    public void A_task_dropped_on_a_heading_goes_first_in_that_section()
    {
        var presenter = InProject(grouped: true);

        Assert.True(Dropped(presenter, "x", Over(presenter, "section:s1", DropZone.After)));

        Assert.Equal(["Loose", "Admin", "X", "A", "Under A", "B", "Errands", "Empty"], Shown(presenter));
    }

    [Fact]
    public void A_task_dropped_on_an_empty_sections_heading_moves_into_it()
    {
        var presenter = InProject(grouped: true);

        Assert.True(Dropped(presenter, "a", Over(presenter, "section:s3", DropZone.Before)));

        // Its sub-task goes with it.
        Assert.Equal(["Loose", "Admin", "B", "Errands", "X", "Empty", "A", "Under A"], Shown(presenter));
        Assert.Equal("s3", presenter.Rows.Single(r => r.Id == "u").SectionId);
    }

    [Fact]
    public void A_task_dropped_on_its_own_heading_when_its_first_there_is_not_offered()
    {
        var presenter = InProject(grouped: true);
        var target = Over(presenter, "section:s1", DropZone.Onto);

        Assert.False(presenter.CanDrop("a", target));
        Assert.False(presenter.Drop("a", target));
        Assert.Empty(presenter.History.Recent());
    }

    [Fact]
    public void A_task_dropped_beside_one_in_no_section_leaves_its_section()
    {
        var presenter = InProject(grouped: true);

        Assert.True(Dropped(presenter, "x", Over(presenter, "n", DropZone.After)));

        Assert.Equal(["Loose", "X", "Admin", "A", "Under A", "B", "Errands", "Empty"], Shown(presenter));
        Assert.Null(presenter.Rows.Single(r => r.Id == "x").SectionId);
    }

    [Fact]
    public void A_drop_between_sections_is_noted_in_what_you_have_done()
    {
        var presenter = InProject(grouped: true);

        Dropped(presenter, "x", Over(presenter, "section:s1", DropZone.Onto));

        Assert.Equal(["Moved “X”"], presenter.History.Recent().Select(e => e.Said));
    }

    [Fact]
    public void Ungrouped_the_same_drop_only_reorders_the_project()
    {
        // Nothing on screen says where one section ends, so a drop is about the order alone.
        var presenter = InProject();

        Assert.True(Dropped(presenter, "b", Over(presenter, "x", DropZone.After)));

        Assert.Equal("s1", presenter.Rows.Single(r => r.Id == "b").SectionId);
        Assert.Equal(["Loose", "A", "Under A", "X", "B"], Shown(presenter));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>Drops a task, having asked first whether it could be, the way a drag does.</summary>
    private static bool Dropped(MainPresenter presenter, string id, DropTarget target)
    {
        var offered = presenter.CanDrop(id, target);
        var dropped = presenter.Drop(id, target);

        Assert.Equal(offered, dropped);
        return dropped;
    }

    private static DropTarget Over(MainPresenter presenter, string id, DropZone zone)
    {
        var index = presenter.Rows.ToList().FindIndex(r => r.Id == id);
        return OutlineDrop.Over(presenter.Rows, index, zone)!;
    }

    private static string[] Shown(MainPresenter presenter) => presenter.Rows.Select(r => r.Content).ToArray();

    private static MainPresenter InProject(bool grouped = false) => InProject(Store(), grouped);

    private static MainPresenter InProject(InMemorySnapshotStore store, bool grouped = false)
    {
        var presenter = NewPresenter(store);
        presenter.SetGroupBySection(grouped);
        presenter.Select(ViewSelection.OfProject("p"));
        return presenter;
    }

    /// <summary>
    /// Project "p", holding "Loose" in no section, "A" (with "Under A") and "B" in "Admin", "X" in
    /// "Errands", and nothing in "Empty". Project "q" has no sections at all.
    /// </summary>
    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "inbox", """{"id":"inbox","name":"Inbox","is_inbox_project":true,"child_order":0}""");
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("projects", "q", """{"id":"q","name":"Home","child_order":2}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p","section_order":1}""");
        store.PutResource("sections", "s2", """{"id":"s2","name":"Errands","project_id":"p","section_order":2}""");
        store.PutResource("sections", "s3", """{"id":"s3","name":"Empty","project_id":"p","section_order":3}""");
        store.PutResource("items", "n", """{"id":"n","content":"Loose","project_id":"p","child_order":0}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        store.PutResource("items", "u", """{"id":"u","content":"Under A","project_id":"p","section_id":"s1","parent_id":"a","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","section_id":"s1","child_order":2}""");
        store.PutResource("items", "x", """{"id":"x","content":"X","project_id":"p","section_id":"s2","child_order":3}""");
        store.PutResource("items", "h", """{"id":"h","content":"H","project_id":"q","child_order":1}""");
        return store;
    }

    private static MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
    }
}
