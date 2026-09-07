using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Hiding what is filed under a task, and showing it again.
/// </summary>
/// <remarks>
/// The rows the outline draws are the whole of what this changes: nothing here reaches the account,
/// and a folded task is still every bit as much in the project as it was. So these ask what is on
/// screen rather than what was stored.
/// </remarks>
public class SubtaskFoldingTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>
    /// Two top-level tasks, one of them three deep, the other with a child of its own.
    /// </summary>
    /// <remarks>
    /// The sibling matters: folding one task has to leave the other's children where they are, and
    /// a fixture with only one parent in it can't tell a fold from a purge.
    /// </remarks>
    private static MainPresenter Nested()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Child","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"Grandchild","project_id":"p","parent_id":"b","child_order":1}""");
        store.PutResource("items", "d", """{"id":"d","content":"Other","project_id":"p","child_order":2}""");
        store.PutResource("items", "e", """{"id":"e","content":"Other child","project_id":"p","parent_id":"d","child_order":1}""");
        return All(store);
    }

    private static string[] Shown(MainPresenter presenter) => presenter.Rows.Select(r => r.Content).ToArray();

    // ---- What has something to fold ----------------------------------------------------------

    [Fact]
    public void Only_a_task_with_something_under_it_is_offered_an_expander()
    {
        var presenter = Nested();

        var offered = presenter.Rows.Where(r => r.HasChildren).Select(r => r.Content).ToArray();

        Assert.Equal(new[] { "Parent", "Child", "Other" }, offered);
    }

    [Fact]
    public void A_task_whose_children_are_not_in_this_view_claims_none()
    {
        // The child is due today and the parent isn't, so only the child is in the Today view — and
        // it stands on its own there. Nothing above it is claiming to hold it.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Child","project_id":"p","parent_id":"a","child_order":1,"due":{"date":"2026-07-31"}}""");
        var presenter = NewPresenter(store);

        Assert.All(presenter.Rows, r => Assert.False(r.HasChildren));
    }

    // ---- Folding -------------------------------------------------------------------------------

    [Fact]
    public void Folding_a_task_takes_everything_under_it_off_the_list()
    {
        // Everything, not just the row below: the grandchild goes with the child it belongs to.
        var presenter = Nested();

        Assert.True(presenter.SetCollapsed("a", true));

        Assert.Equal(new[] { "Parent", "Other", "Other child" }, Shown(presenter));
    }

    [Fact]
    public void Folding_one_task_leaves_another_alone()
    {
        var presenter = Nested();

        presenter.SetCollapsed("a", true);

        Assert.Contains("Other child", Shown(presenter));
    }

    [Fact]
    public void Folding_deeper_down_keeps_what_is_above_it()
    {
        // The child holds the grandchild; folding the child hides one row and no more.
        var presenter = Nested();

        presenter.SetCollapsed("b", true);

        Assert.Equal(new[] { "Parent", "Child", "Other", "Other child" }, Shown(presenter));
    }

    [Fact]
    public void Unfolding_puts_it_all_back()
    {
        var presenter = Nested();
        var before = Shown(presenter);

        presenter.SetCollapsed("a", true);
        Assert.True(presenter.SetCollapsed("a", false));

        Assert.Equal(before, Shown(presenter));
    }

    [Fact]
    public void The_folded_task_says_that_it_is()
    {
        var presenter = Nested();

        presenter.SetCollapsed("a", true);

        Assert.True(presenter.Rows.Single(r => r.Id == "a").Collapsed);
        Assert.False(presenter.Rows.Single(r => r.Id == "d").Collapsed);
        Assert.True(presenter.IsCollapsed("a"));
    }

    [Fact]
    public void Asking_for_what_is_already_so_changes_nothing()
    {
        // The caller uses this to leave the list alone, and a list rebuilt for nothing moves the
        // selection and the viewport under whoever is reading it.
        var presenter = Nested();

        Assert.True(presenter.SetCollapsed("a", true));
        Assert.False(presenter.SetCollapsed("a", true));

        Assert.False(presenter.SetCollapsed("d", false));
    }

    [Fact]
    public void A_fold_outlives_a_sort()
    {
        // Sorting republishes the rows it already has, and the fold is one of the things said about
        // them rather than something the projection went and worked out again.
        var presenter = Nested();
        presenter.SetCollapsed("a", true);

        presenter.SortBy(TaskColumn.Content);

        Assert.DoesNotContain("Grandchild", Shown(presenter));
        Assert.True(presenter.IsCollapsed("a"));
    }

    [Fact]
    public void A_fold_outlives_a_sync()
    {
        // A background sync republishes every forty-five seconds. A fold undone by one would come
        // undone on its own while somebody was looking at it.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Child","project_id":"p","parent_id":"a","child_order":1}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));

        presenter.SetCollapsed("a", true);
        engine.Load();

        Assert.Equal(new[] { "Parent" }, Shown(presenter));
    }

    // ---- All at once ---------------------------------------------------------------------------

    [Fact]
    public void Folding_the_lot_reaches_every_depth()
    {
        // Recursive by nature rather than by recursing: what it walks is the whole outline for the
        // view, so a task three levels down is as reachable as one at the top.
        var presenter = Nested();

        Assert.True(presenter.FoldAll(collapsed: true));

        Assert.Equal(new[] { "Parent", "Other" }, Shown(presenter));
        Assert.True(presenter.IsCollapsed("b"));
    }

    [Fact]
    public void Unfolding_the_lot_opens_what_was_folded_at_any_depth()
    {
        var presenter = Nested();
        presenter.FoldAll(collapsed: true);

        Assert.True(presenter.FoldAll(collapsed: false));

        Assert.Equal(new[] { "Parent", "Child", "Grandchild", "Other", "Other child" }, Shown(presenter));
    }

    [Fact]
    public void Folding_the_lot_leaves_other_views_as_they_were()
    {
        // The thing that was asked for: this view and not the account. A project nobody has open
        // has no business being rearranged by a menu entry aimed at the one that is.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Home","child_order":2}""");
        store.PutResource("items", "w", """{"id":"w","content":"Work parent","project_id":"p1","child_order":1}""");
        store.PutResource("items", "wc", """{"id":"wc","content":"Work child","project_id":"p1","parent_id":"w","child_order":1}""");
        store.PutResource("items", "h", """{"id":"h","content":"Home parent","project_id":"p2","child_order":1}""");
        store.PutResource("items", "hc", """{"id":"hc","content":"Home child","project_id":"p2","parent_id":"h","child_order":1}""");

        var presenter = NewPresenter(store);

        // Fold everything in Work, then go to Home and find it as it was left.
        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.FoldAll(collapsed: true);

        presenter.Select(ViewSelection.OfProject("p2"));
        Assert.Equal(new[] { "Home parent", "Home child" }, Shown(presenter));

        // And unfolding Home leaves Work folded, which is the same rule the other way round.
        presenter.FoldAll(collapsed: false);
        presenter.Select(ViewSelection.OfProject("p1"));

        Assert.Equal(new[] { "Work parent" }, Shown(presenter));
    }

    [Fact]
    public void Folding_the_lot_when_it_is_all_folded_changes_nothing()
    {
        var presenter = Nested();

        Assert.True(presenter.FoldAll(collapsed: true));
        Assert.False(presenter.FoldAll(collapsed: true));
    }

    [Fact]
    public void There_is_nothing_to_fold_in_a_view_with_no_subtasks()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Alone","project_id":"p","child_order":1}""");
        var presenter = All(store);

        Assert.False(presenter.CanCollapseAll);
        Assert.False(presenter.CanExpandAll);
        Assert.False(presenter.FoldAll(collapsed: true));
    }

    [Fact]
    public void What_can_be_folded_says_which_way_the_view_stands()
    {
        // What greys the two menu entries, and between them they say whether anything is open.
        var presenter = Nested();

        Assert.True(presenter.CanCollapseAll);
        Assert.False(presenter.CanExpandAll);

        presenter.FoldAll(collapsed: true);

        Assert.False(presenter.CanCollapseAll);
        Assert.True(presenter.CanExpandAll);
    }

    // ---- Where the selection goes --------------------------------------------------------------

    [Fact]
    public void A_task_folded_out_of_sight_says_what_took_its_place()
    {
        var presenter = Nested();
        presenter.SetCollapsed("a", true);

        Assert.Equal("a", presenter.NearestShown("c"));
    }

    [Fact]
    public void It_is_the_nearest_ancestor_still_on_screen_and_not_the_parent()
    {
        // The grandchild's parent is folded away itself, so answering with it would put the
        // selection on a row that isn't there either.
        var presenter = Nested();
        presenter.SetCollapsed("b", true);
        presenter.SetCollapsed("a", true);

        Assert.Equal("a", presenter.NearestShown("c"));
    }

    [Fact]
    public void A_task_still_on_screen_is_not_replaced_by_anything()
    {
        // Asked only of a task that has gone, but a top-level one has no ancestor to offer either.
        var presenter = Nested();

        Assert.Null(presenter.NearestShown("a"));
        Assert.Null(presenter.NearestShown("nothing at all"));
    }

    // ---- Searching -----------------------------------------------------------------------------

    [Fact]
    public void A_search_result_is_flat_and_offers_nothing_to_fold()
    {
        // Matches rarely share a parent, so the results are a flat list — and an expander on one of
        // them would offer to hide children that were never in the list to begin with.
        var presenter = Nested();

        presenter.Search("child");

        Assert.NotEmpty(presenter.Rows);
        Assert.All(presenter.Rows, r => Assert.False(r.HasChildren));
    }

    [Fact]
    public void A_fold_does_not_hide_a_search_result()
    {
        // The fold is about the outline's nesting, and a result list has none. A match that went
        // missing because something it isn't shown under is folded would read as no match at all.
        var presenter = Nested();
        presenter.SetCollapsed("a", true);

        presenter.Search("Grandchild");

        Assert.Equal(new[] { "Grandchild" }, Shown(presenter));
    }

    [Fact]
    public void The_fold_is_still_there_when_the_search_is_let_go()
    {
        var presenter = Nested();
        presenter.SetCollapsed("a", true);

        presenter.Search("Grandchild");
        presenter.Search(string.Empty);

        Assert.Equal(new[] { "Parent", "Other", "Other child" }, Shown(presenter));
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
