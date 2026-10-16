using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Which drop the pointer is asking for over the outline's rows.
/// </summary>
/// <remarks>
/// Whether the account will take it is the engine's half, tested in <c>DropPlacementTests</c>.
/// </remarks>
public class OutlineDropTests
{
    /// <summary>
    /// A parent unfolded over two sub-tasks, the second with one of its own, then a top-level task
    /// with nothing under it.
    /// </summary>
    private static readonly IReadOnlyList<TaskRow> Rows =
    [
        Task("a", depth: 0),
        Task("b", depth: 1),
        Task("c", depth: 1),
        Task("d", depth: 2),
        Task("e", depth: 0),
    ];

    private static TaskRow Task(string id, int depth, bool completed = false)
        => new(id, id.ToUpperInvariant(), Priority.P4, "Work", string.Empty, [], depth, Completed: completed);

    // ---- Which part of a row ---------------------------------------------------------------

    [Theory]
    [InlineData(0, DropZone.Before)]
    [InlineData(6, DropZone.Before)]
    [InlineData(7, DropZone.Onto)]
    [InlineData(13, DropZone.Onto)]
    [InlineData(14, DropZone.After)]
    [InlineData(20, DropZone.After)]
    public void A_row_is_split_into_thirds(int y, DropZone expected)
        => Assert.Equal(expected, OutlineDrop.ZoneAt(y, height: 21));

    [Fact]
    public void Above_and_below_a_row_count_as_its_edges()
    {
        Assert.Equal(DropZone.Before, OutlineDrop.ZoneAt(-3, height: 21));
        Assert.Equal(DropZone.After, OutlineDrop.ZoneAt(25, height: 21));
    }

    // ---- Over a row ----------------------------------------------------------------------------

    [Fact]
    public void Before_and_onto_a_row_are_that_row_at_its_own_level()
    {
        Assert.Equal(new DropTarget("c", DropZone.Before, 2, 1), OutlineDrop.Over(Rows, 2, DropZone.Before));
        Assert.Equal(new DropTarget("c", DropZone.Onto, 2, 1), OutlineDrop.Over(Rows, 2, DropZone.Onto));
    }

    [Fact]
    public void After_an_unfolded_task_is_before_its_first_sub_task()
    {
        // The gap below "a" is the gap above "b", drawn under it, so that's where the task goes.
        Assert.Equal(new DropTarget("b", DropZone.Before, 1, 1), OutlineDrop.Over(Rows, 0, DropZone.After));
    }

    [Fact]
    public void After_a_task_with_nothing_drawn_under_it_is_after_that_task()
    {
        // "b" is followed by its sibling, and "d" by a task two levels up: neither is under it.
        Assert.Equal(new DropTarget("b", DropZone.After, 1, 1), OutlineDrop.Over(Rows, 1, DropZone.After));
        Assert.Equal(new DropTarget("d", DropZone.After, 3, 2), OutlineDrop.Over(Rows, 3, DropZone.After));
    }

    [Fact]
    public void After_the_last_row_is_after_that_task()
        => Assert.Equal(new DropTarget("e", DropZone.After, 4, 0), OutlineDrop.Over(Rows, 4, DropZone.After));

    [Fact]
    public void A_heading_or_a_row_that_is_not_there_is_nothing_to_drop_by()
    {
        TaskRow heading = new("day:2026-08-01", "1 Aug · Tomorrow", Priority.P4, string.Empty, string.Empty, [], IsHeading: true);

        Assert.Null(OutlineDrop.Over([heading, Task("a", 0)], 0, DropZone.Onto));
        Assert.Null(OutlineDrop.Over(Rows, -1, DropZone.Before));
        Assert.Null(OutlineDrop.Over(Rows, Rows.Count, DropZone.Before));
    }

    // ---- Below the rows ------------------------------------------------------------------------

    [Fact]
    public void Below_the_rows_is_after_the_last_top_level_task()
        => Assert.Equal(new DropTarget("e", DropZone.After, 4, 0), OutlineDrop.Below(Rows));

    [Fact]
    public void Below_the_rows_is_marked_under_the_last_top_level_tasks_sub_tasks()
    {
        // Nothing in the last task's own zones reaches the end of the list at the top level: after
        // it is its first sub-task's place, and after its last sub-task is that sub-task's level.
        IReadOnlyList<TaskRow> rows = [Task("a", 0), Task("b", 0), Task("c", 1), Task("d", 2)];

        Assert.Equal(new DropTarget("b", DropZone.After, 3, 0), OutlineDrop.Below(rows));
    }

    [Fact]
    public void Below_the_rows_passes_over_the_finished_tasks_listed_at_the_bottom()
    {
        IReadOnlyList<TaskRow> rows = [Task("a", 0), Task("b", 1), Task("x", 0, completed: true), Task("y", 0, completed: true)];

        Assert.Equal(new DropTarget("a", DropZone.After, 1, 0), OutlineDrop.Below(rows));
    }

    [Fact]
    public void Below_no_task_still_to_do_is_nothing_to_drop_by()
    {
        Assert.Null(OutlineDrop.Below([]));
        Assert.Null(OutlineDrop.Below([Task("x", 0, completed: true)]));
    }

    // ---- Whether it moves anything -------------------------------------------------------------

    [Fact]
    public void Next_to_where_a_task_already_is_moves_nothing()
    {
        // Above the next task at its level, and below the one before it, whatever's under either.
        Assert.False(OutlineDrop.Moves(Rows, "b", new DropTarget("c", DropZone.Before, 2, 1)));
        Assert.False(OutlineDrop.Moves(Rows, "c", new DropTarget("b", DropZone.After, 1, 1)));
        Assert.False(OutlineDrop.Moves(Rows, "a", new DropTarget("e", DropZone.Before, 4, 0)));
        Assert.False(OutlineDrop.Moves(Rows, "e", new DropTarget("a", DropZone.After, 0, 0)));
    }

    [Fact]
    public void Anywhere_else_moves_it()
    {
        Assert.True(OutlineDrop.Moves(Rows, "c", new DropTarget("b", DropZone.Before, 1, 1)));
        Assert.True(OutlineDrop.Moves(Rows, "d", new DropTarget("c", DropZone.Before, 2, 1)));

        // Out from under "a", though "b" is the row just above: it's at another level.
        Assert.True(OutlineDrop.Moves(Rows, "b", new DropTarget("e", DropZone.Before, 4, 0)));

        // Under another parent, though "b" is the nearest row above at that level.
        IReadOnlyList<TaskRow> two = [Task("a", 0), Task("b", 1), Task("c", 0), Task("d", 1)];
        Assert.True(OutlineDrop.Moves(two, "b", new DropTarget("d", DropZone.Before, 3, 1)));
    }

    [Fact]
    public void Onto_a_task_is_left_to_the_engine()
        => Assert.True(OutlineDrop.Moves(Rows, "b", new DropTarget("a", DropZone.Onto, 0, 0)));
}
