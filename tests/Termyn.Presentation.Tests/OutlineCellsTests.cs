using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// What each cell of a task's row says, and how the outline fills it.
/// </summary>
/// <remarks>
/// The words rather than the pixels: a list in virtual owner-draw mode won't render its rows into a
/// bitmap. Which columns the outline stands, and the cells it hands the control, are asked of the
/// control itself in <c>OutlineColumnTests</c>.
/// </remarks>
public class OutlineCellsTests
{
    private static TaskRow Row() => new(
        "t1",
        "Plan the week",
        Priority.P2,
        "Work",
        "1 Aug",
        ["followup"],
        Deadline: "4 Aug");

    [Fact]
    public void Each_column_reads_the_part_of_the_row_it_stands_for()
    {
        var row = Row();

        Assert.Equal("Plan the week", OutlineCells.CellOf(row, TaskColumn.Content));
        Assert.Equal("Work", OutlineCells.CellOf(row, TaskColumn.Project));
        Assert.Equal("1 Aug", OutlineCells.CellOf(row, TaskColumn.Due));
        Assert.Equal("4 Aug", OutlineCells.CellOf(row, TaskColumn.Deadline));
        Assert.Equal("@followup", OutlineCells.CellOf(row, TaskColumn.Labels));

        // The priority is a flag, so there are no words to hand over for it.
        Assert.Equal(string.Empty, OutlineCells.CellOf(row, TaskColumn.Priority));
    }

    [Fact]
    public void Each_column_is_filled_the_way_it_says()
    {
        // The drawing itself can't be asserted, so this holds the decision that routes it. A column
        // that stopped being painted would write the same words with the colour gone — the
        // project's dot and the labels' own colours are what would go missing without a sound.
        Assert.Equal(
            [CellPaint.Written, CellPaint.Priority, CellPaint.Project, CellPaint.Written, CellPaint.Written, CellPaint.Labels],
            new[] { TaskColumn.Content, TaskColumn.Priority, TaskColumn.Project, TaskColumn.Due, TaskColumn.Deadline, TaskColumn.Labels }
                .Select(OutlineCells.PaintOf));
    }

    [Fact]
    public void Labels_read_the_way_quick_add_writes_them()
    {
        Assert.Equal("@followup @home", OutlineCells.LabelsOf(Row() with { Labels = ["followup", "home"] }));
        Assert.Equal(string.Empty, OutlineCells.LabelsOf(Row() with { Labels = [] }));
    }

    [Fact]
    public void A_task_with_a_conversation_is_marked_beside_its_name()
    {
        // Nowhere else says there's anything on the Comments tab until it's opened.
        Assert.Equal("Plan the week  💬", OutlineCells.CellOf(Row() with { CommentCount = 2 }, TaskColumn.Content));
    }

    [Fact]
    public void A_repeat_and_a_reminder_are_marked_in_front_of_the_due_date()
    {
        Assert.Equal("↻ 1 Aug", OutlineCells.CellOf(Row() with { IsRecurring = true }, TaskColumn.Due));
        Assert.Equal("⏰ 1 Aug", OutlineCells.CellOf(Row() with { ReminderCount = 1 }, TaskColumn.Due));
        Assert.Equal("↻⏰ 1 Aug", OutlineCells.CellOf(Row() with { IsRecurring = true, ReminderCount = 3 }, TaskColumn.Due));
    }

    [Fact]
    public void A_recurring_task_waiting_for_its_next_date_says_so_in_place_of_the_old_one()
    {
        // The date it's leaving would say the press did nothing.
        Assert.Equal("↻ advancing…", OutlineCells.CellOf(Row() with { IsRecurring = true, Advancing = true }, TaskColumn.Due));
        Assert.Equal("↻⏰ advancing…", OutlineCells.CellOf(Row() with { IsRecurring = true, ReminderCount = 1, Advancing = true }, TaskColumn.Due));
    }

    [Fact]
    public void A_recurring_task_waiting_for_its_next_date_is_drawn_finished()
    {
        // Struck through like any task that's been ticked off, so the press visibly did something.
        Assert.True(OutlineCells.DrawnDone(Row() with { IsRecurring = true, Advancing = true }));
        Assert.True(OutlineCells.DrawnDone(Row() with { Completed = true }));
        Assert.False(OutlineCells.DrawnDone(Row() with { IsRecurring = true }));
    }

    [Fact]
    public void A_mark_with_no_due_date_to_go_with_it_stands_on_its_own()
    {
        // Without a date there's nothing to space it from, and a space in front would push the mark
        // off the column's edge.
        Assert.Equal("⏰", OutlineCells.CellOf(Row() with { Due = string.Empty, ReminderCount = 1 }, TaskColumn.Due));
    }
}
