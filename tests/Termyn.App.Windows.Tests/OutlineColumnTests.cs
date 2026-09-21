using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Which columns the outline stands, and what each of them reads.
/// </summary>
/// <remarks>
/// The words rather than the pixels, for the reason <see cref="OutlineColourTests"/> gives: a list
/// in virtual owner-draw mode won't render its rows into a bitmap. What these do cover is the part
/// that went wrong when a column was added — a cell drawn under the heading next to its own.
/// </remarks>
public class OutlineColumnTests
{
    private static TaskRow Row() => new(
        "t1",
        "Plan the week",
        Priority.P2,
        "Work",
        "1 Aug",
        ["followup"],
        Deadline: "4 Aug");

    [WinFormsFact]
    public void The_deadline_stands_in_a_column_of_its_own_beside_the_due_date()
    {
        using var outline = new OutlineView();

        Assert.Equal(
            [
                TaskColumn.Content,
                TaskColumn.Priority,
                TaskColumn.Project,
                TaskColumn.Due,
                TaskColumn.Deadline,
                TaskColumn.Labels,
            ],
            outline.Columns.Cast<ColumnHeader>().Select(c => c.Tag).ToArray());

        // And headed with what it holds, since the column is the only thing saying which date a
        // row's two dates is which.
        Assert.Equal(["Task", "!", "Project", "Due", "Deadline", "Labels"], outline.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray());
    }

    [WinFormsFact]
    public void Each_column_reads_the_part_of_the_row_it_stands_for()
    {
        var row = Row();

        Assert.Equal("Plan the week", OutlineView.CellOf(row, TaskColumn.Content));
        Assert.Equal("Work", OutlineView.CellOf(row, TaskColumn.Project));
        Assert.Equal("1 Aug", OutlineView.CellOf(row, TaskColumn.Due));
        Assert.Equal("4 Aug", OutlineView.CellOf(row, TaskColumn.Deadline));
        Assert.Equal("@followup", OutlineView.CellOf(row, TaskColumn.Labels));

        // The priority is a flag, so there are no words to hand over for it.
        Assert.Equal(string.Empty, OutlineView.CellOf(row, TaskColumn.Priority));
    }

    [WinFormsFact]
    public void A_row_is_handed_over_one_cell_per_column_in_the_order_they_stand()
    {
        // The control is given these as the row's sub-items, which is what a screen reader reads
        // and what an exported row would say. A column added without a cell shows up here as a gap.
        using var outline = new OutlineView();

        Assert.Equal(["Plan the week", string.Empty, "Work", "1 Aug", "4 Aug", "@followup"], outline.Cells(Row()));
    }

    [WinFormsFact]
    public void A_task_with_no_deadline_hands_over_an_empty_cell()
    {
        // The whole row rather than the one cell: read by position, this would go on passing if a
        // column were inserted in front of it and the empty cell being read were some other one's.
        using var outline = new OutlineView();

        Assert.Equal(
            ["Plan the week", string.Empty, "Work", "1 Aug", string.Empty, "@followup"],
            outline.Cells(Row() with { Deadline = string.Empty }));
    }

    [WinFormsFact]
    public void Each_column_is_filled_the_way_it_says()
    {
        // The drawing itself can't be asserted, so this holds the decision that routes it. A column
        // that stopped being painted would write the same words with the colour gone — the
        // project's dot and the labels' own colours are what would go missing without a sound.
        //
        // Named rather than typed: the enum is internal to a control that is itself internal, and a
        // public test method can't take one as an argument.
        using var outline = new OutlineView();

        Assert.Equal(
            ["Written", "Priority", "Project", "Written", "Written", "Labels"],
            outline.Columns.Cast<ColumnHeader>()
                .Select(c => OutlineView.PaintOf((TaskColumn)c.Tag!).ToString())
                .ToArray());
    }
}
