namespace Termyn.Presentation;

/// <summary>How the outline fills a cell: with words, or with one of the marks it paints.</summary>
public enum CellPaint
{
    /// <summary>Written out, which is what a column is unless it's one of the three below.</summary>
    Written,

    Priority,
    Project,
    Labels,
}

/// <summary>
/// What each cell of a task's row says, and how it's filled.
/// </summary>
/// <remarks>
/// Here rather than in the control that draws them: it's the same answer whatever draws it, and
/// it can be tested without standing a control up to ask.
/// </remarks>
public static class OutlineCells
{
    /// <summary>
    /// Which of those a column gets.
    /// </summary>
    /// <remarks>
    /// Kept out of the drawing so a test can hold it to this. The paint itself can't be asserted —
    /// a virtual owner-drawn list won't render its rows into a bitmap — so a column that quietly
    /// stopped being painted would go on writing the same words with the colour gone, and nothing
    /// would fail. Here, dropping one is a test away.
    /// </remarks>
    /// <param name="column">The column being drawn</param>
    /// <returns>What fills its cells</returns>
    public static CellPaint PaintOf(TaskColumn column) => column switch
    {
        TaskColumn.Priority => CellPaint.Priority,
        TaskColumn.Project => CellPaint.Project,
        TaskColumn.Labels => CellPaint.Labels,
        _ => CellPaint.Written,
    };

    /// <summary>
    /// What a cell says, for the columns that are words rather than marks.
    /// </summary>
    /// <remarks>
    /// One table for the text the control is handed and the text that's drawn, so a column can't
    /// end up saying one thing to the screen and another to a screen reader. The priority column
    /// is a flag with no words to it, and answers empty.
    /// </remarks>
    /// <param name="row">The task the cell belongs to</param>
    /// <param name="column">Which of its columns is being asked for</param>
    /// <returns>The words for that cell, or empty when the column is drawn rather than written</returns>
    public static string CellOf(TaskRow row, TaskColumn column) => column switch
    {
        TaskColumn.Content => ContentOf(row),
        TaskColumn.Project => row.Project,
        TaskColumn.Due => DueOf(row),
        TaskColumn.Deadline => row.Deadline,
        TaskColumn.Labels => LabelsOf(row),
        _ => string.Empty,
    };

    /// <summary>Labels as they are written in quick-add, so the row reads the way it was typed.</summary>
    /// <param name="row">The task whose labels to write</param>
    /// <returns>Each label with its @, or empty when it has none</returns>
    public static string LabelsOf(TaskRow row)
        => row.Labels.Count == 0 ? string.Empty : "@" + string.Join(" @", row.Labels);

    /// <summary>
    /// The task's own column: its name, and a mark when there's a conversation on it.
    /// </summary>
    /// <remarks>
    /// Marked here rather than beside the repeat and the reminder, which share the due column
    /// because both are about when the task comes round. A comment isn't about timing at all, and
    /// without a mark somewhere it's invisible until you open the pane.
    /// </remarks>
    private static string ContentOf(TaskRow row)
        => row.CommentCount > 0 ? $"{row.Content}  💬" : row.Content;

    /// <summary>
    /// The due column. A repeat and a reminder are marked here rather than given columns of their
    /// own: both are about when the task comes round, and neither is worth the width.
    /// </summary>
    private static string DueOf(TaskRow row)
    {
        var marks = (row.IsRecurring ? "↻" : string.Empty) + (row.ReminderCount > 0 ? "⏰" : string.Empty);
        if (marks.Length == 0)
            return row.Due;

        return row.Due.Length == 0 ? marks : $"{marks} {row.Due}";
    }
}
