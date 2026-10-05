using Termyn.Core.Settings;

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

    /// <summary>
    /// Whether a row is drawn finished: greyed, struck through, and its box ticked.
    /// </summary>
    /// <remarks>
    /// A recurring task that's advancing is drawn the same way. As far as the press goes it's done,
    /// and all that's left is the server saying when it comes round next. Until then a row that
    /// looked untouched would say the press hadn't taken.
    /// </remarks>
    /// <param name="row">The task the row belongs to</param>
    /// <returns>True for a finished task, or a recurring one on its way to its next date</returns>
    public static bool DrawnDone(TaskRow row) => row.Completed || row.Advancing;

    /// <summary>
    /// The dot in front of a row's project, or null when there's none to draw.
    /// </summary>
    /// <remarks>
    /// Nothing on a selected row: the accent is behind it, and a colour chosen to read against the
    /// panel has made no promise about that. A task in no project has no colour either — the row
    /// carries one only when it found the project — so that answers itself.
    /// </remarks>
    /// <param name="row">The task the cell belongs to</param>
    /// <param name="selected">Whether the row is drawn selected</param>
    /// <returns>The project's colour, or null for no dot</returns>
    public static Rgb? ProjectDot(TaskRow row, bool selected) => selected ? null : row.ProjectColour;

    /// <summary>
    /// The labels of a row, each with the colour it's written in.
    /// </summary>
    /// <remarks>
    /// A selected row comes back as one run in the muted colour: the accent behind it is what the
    /// row is saying, and five colours over it say less than none. A label the window hasn't been
    /// told the colour of — one just made, before the sync describing it — reads as it always did.
    /// </remarks>
    /// <typeparam name="TColour">
    /// Whatever the colours are held as — generic so something drawing the rows can convert them
    /// into its own toolkit's type once, rather than on every paint
    /// </typeparam>
    /// <param name="row">The task the cell belongs to</param>
    /// <param name="selected">Whether the row is drawn selected</param>
    /// <param name="colours">What each label is coloured with, by name</param>
    /// <returns>Each run of text with its colour, or null for the muted colour whatever draws it</returns>
    public static IReadOnlyList<(string Text, TColour? Colour)> LabelRuns<TColour>(
        TaskRow row,
        bool selected,
        IReadOnlyDictionary<string, TColour> colours)
        where TColour : struct
    {
        if (selected || row.Labels.Count == 0)
            return LabelsOf(row) is { Length: > 0 } all ? [(all, null)] : [];

        return row.Labels
            .Select(l => ("@" + l, colours.TryGetValue(l, out var found) ? found : (TColour?)null))
            .ToList();
    }

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
    /// <remarks>
    /// A recurring task that's been ticked off reads <c>advancing…</c> in place of its date until
    /// the server says where it lands next. The date it's leaving would say the press did nothing.
    /// </remarks>
    private static string DueOf(TaskRow row)
    {
        var due = row.Advancing ? Strings.DueAdvancing : row.Due;
        var marks = (row.IsRecurring ? "↻" : string.Empty) + (row.ReminderCount > 0 ? "⏰" : string.Empty);
        if (marks.Length == 0)
            return due;

        return due.Length == 0 ? marks : $"{marks} {due}";
    }
}
