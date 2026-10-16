using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Dragging tasks about the outline: where it's allowed, and what a drop does to the list.
/// </summary>
/// <remarks>
/// Which drop a pointer asks for is <c>OutlineDropTests</c>, and whether the account takes it is
/// <c>DropPlacementTests</c>. These are the presenter's half between the two.
/// </remarks>
public class DraggingTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- Where it's allowed ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(OneProject))]
    public void Tasks_can_be_dragged_where_the_outline_shows_one_project(ViewSelection selection)
    {
        var presenter = NewPresenter(Store());

        presenter.Select(selection);

        Assert.True(presenter.CanDrag);
    }

    public static TheoryData<ViewSelection> OneProject =>
    [
        ViewSelection.OfProject("p"),
        ViewSelection.OfSection("s1"),
        ViewSelection.Of(SmartView.Inbox),
    ];

    [Theory]
    [MemberData(nameof(AcrossProjects))]
    public void Tasks_cannot_be_dragged_where_the_outline_mixes_projects(ViewSelection selection)
    {
        // Two tasks side by side here can be from different projects, so a drop between them would
        // move a task to another project when all anyone meant was to reorder the view.
        var presenter = NewPresenter(Store());

        presenter.Select(selection);

        Assert.False(presenter.CanDrag);
    }

    public static TheoryData<ViewSelection> AcrossProjects =>
    [
        ViewSelection.Of(SmartView.Today),
        ViewSelection.Of(SmartView.Upcoming),
        ViewSelection.Of(SmartView.All),
        ViewSelection.OfLabel("followup"),
        ViewSelection.OfFilter("f1"),
    ];

    [Fact]
    public void Tasks_cannot_be_dragged_while_the_outline_is_sorted()
    {
        var presenter = InProject();

        presenter.SortBy(TaskColumn.Content);
        Assert.False(presenter.CanDrag);

        presenter.ClearSort();
        Assert.True(presenter.CanDrag);
    }

    [Fact]
    public void Tasks_cannot_be_dragged_while_searching()
    {
        var presenter = InProject();

        presenter.Search("B");
        Assert.False(presenter.CanDrag);

        presenter.Search(string.Empty);
        Assert.True(presenter.CanDrag);
    }

    // ---- What a drop does ----------------------------------------------------------------------

    [Fact]
    public void A_task_dropped_above_another_is_shown_there()
    {
        var presenter = InProject();

        Assert.True(presenter.Drop("c", OutlineDrop.Over(presenter.Rows, 0, DropZone.Before)!));

        Assert.Equal(["C", "A", "Under A", "B"], Shown(presenter));
    }

    [Fact]
    public void A_task_dropped_onto_a_folded_one_unfolds_it_to_show_where_it_went()
    {
        var presenter = InProject();
        presenter.SetCollapsed("a", true);

        Assert.True(presenter.Drop("b", Onto(presenter, "a")));

        Assert.False(presenter.IsCollapsed("a"));
        Assert.Equal(["A", "Under A", "B", "C"], Shown(presenter));
        Assert.Equal(1, presenter.Rows.Single(r => r.Id == "b").Depth);
    }

    [Fact]
    public void A_drop_is_noted_in_what_you_have_done()
    {
        var presenter = InProject();

        presenter.Drop("c", OutlineDrop.Over(presenter.Rows, 0, DropZone.Before)!);
        presenter.Drop("b", Onto(presenter, "a"));

        Assert.Equal(["Moved “B” under “A”", "Moved “C”"], presenter.History.Recent().Select(e => e.Said));
    }

    [Fact]
    public void Two_drops_of_the_same_task_are_two_lines()
    {
        // Key nudges run together, three presses being one move. Each drop is a whole move.
        var presenter = InProject();

        presenter.Drop("c", OutlineDrop.Over(presenter.Rows, 0, DropZone.Before)!);
        presenter.Drop("c", OutlineDrop.Over(presenter.Rows, 3, DropZone.After)!);

        Assert.Equal(2, presenter.History.Recent().Count);
    }

    [Fact]
    public void A_drop_is_turned_away_where_dragging_is_off()
    {
        // The engine would take this one, so it's the presenter turning it away.
        var presenter = InProject();
        var target = OutlineDrop.Over(presenter.Rows, 0, DropZone.Before)!;
        presenter.SortBy(TaskColumn.Content);

        Assert.False(presenter.CanDrop("c", target));
        Assert.False(presenter.Drop("c", target));
        Assert.Empty(presenter.History.Recent());
    }

    [Fact]
    public void In_a_section_a_drop_that_leaves_it_looking_the_same_is_not_offered()
    {
        // Held in the project as A, X, B, with X in another section. Just above B in this section's
        // view is below X, so the engine would take it, reorder the project, and leave this view
        // exactly as it was.
        var store = Store();
        store.PutResource("sections", "s2", """{"id":"s2","name":"Errands","project_id":"p"}""");
        store.PutResource("items", "sa", """{"id":"sa","content":"First","project_id":"p","section_id":"s1","child_order":11}""");
        store.PutResource("items", "sx", """{"id":"sx","content":"Elsewhere","project_id":"p","section_id":"s2","child_order":12}""");
        store.PutResource("items", "sb", """{"id":"sb","content":"Second","project_id":"p","section_id":"s1","child_order":13}""");
        var presenter = NewPresenter(store);
        presenter.Select(ViewSelection.OfSection("s1"));

        var aboveSecond = OutlineDrop.Over(presenter.Rows, 1, DropZone.Before)!;

        Assert.False(presenter.CanDrop("sa", aboveSecond));
        Assert.False(presenter.Drop("sa", aboveSecond));
        Assert.Empty(presenter.History.Recent());

        // Whereas the other way round does move something.
        Assert.True(presenter.Drop("sb", OutlineDrop.Over(presenter.Rows, 0, DropZone.Before)!));
        Assert.Equal(["Second", "First"], Shown(presenter));
    }

    [Fact]
    public async Task A_task_renamed_by_a_sync_mid_drag_is_still_known_where_it_already_is()
    {
        // Captured offline, it goes by a name of ours until a sync learns the server's, and that can
        // land with the drag under way. In this section it sits just above "Second", but the project
        // holds tasks from outside the section in between, so the engine would take a drop there:
        // only the rows know it changes nothing, and only under the name the rows now use.
        var store = Store();
        store.PutResource("items", "sb", """{"id":"sb","content":"Second","project_id":"p","section_id":"s1","child_order":11}""");
        var api = new FakeApi();
        var presenter = NewPresenter(store, api);
        presenter.Select(ViewSelection.OfSection("s1"));

        await presenter.CaptureAsync("Fresh");
        var ours = presenter.Rows.Single(r => r.Content == "Fresh").Id;

        api.Next = FakeApi.Naming(new Dictionary<string, string> { ["item_add"] = "fresh" });
        await presenter.SyncAsync();
        Assert.Equal(["fresh", "sb"], presenter.Rows.Select(r => r.Id));

        var aboveSecond = OutlineDrop.Over(presenter.Rows, 1, DropZone.Before)!;

        Assert.False(presenter.CanDrop(ours, aboveSecond));
        Assert.False(presenter.Drop(ours, aboveSecond));
        Assert.DoesNotContain(presenter.History.Recent(), e => e.Said.StartsWith("Moved", StringComparison.Ordinal));
    }

    [Fact]
    public void A_drop_the_engine_refuses_is_not_offered_and_changes_nothing()
    {
        var presenter = InProject();
        var target = Onto(presenter, "u");

        Assert.False(presenter.CanDrop("a", target));
        Assert.False(presenter.Drop("a", target));
        Assert.Equal(["A", "Under A", "B", "C"], Shown(presenter));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static DropTarget Onto(MainPresenter presenter, string id)
    {
        var index = presenter.Rows.ToList().FindIndex(r => r.Id == id);
        return OutlineDrop.Over(presenter.Rows, index, DropZone.Onto)!;
    }

    private static string[] Shown(MainPresenter presenter) => presenter.Rows.Select(r => r.Content).ToArray();

    /// <summary>Project "p" in view: "a" with "u" under it, then "b" and "c".</summary>
    private static MainPresenter InProject()
    {
        var presenter = NewPresenter(Store());
        presenter.Select(ViewSelection.OfProject("p"));
        return presenter;
    }

    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "inbox", """{"id":"inbox","name":"Inbox","is_inbox_project":true,"child_order":0}""");
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p"}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup"}""");
        store.PutResource("filters", "f1", """{"id":"f1","name":"Soon","query":"today"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "u", """{"id":"u","content":"Under A","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","child_order":3}""");
        return store;
    }

    private static MainPresenter NewPresenter(InMemorySnapshotStore store, FakeApi? api = null)
    {
        var engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
    }
}
