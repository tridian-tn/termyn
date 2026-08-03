namespace Termyn.Presentation;

/// <summary>A column of the outline, as something to order it by.</summary>
public enum TaskColumn
{
    /// <summary>No column — the account's own order, which is what the outline shows to begin with.</summary>
    None,

    Content,
    Priority,
    Project,
    Due,
    Labels,
}

/// <summary>
/// How the outline is ordered. A column orders each set of siblings; the nesting is the account's
/// and stays put whichever column is chosen.
/// </summary>
public sealed record TaskSort(TaskColumn Column = TaskColumn.None, bool Descending = false)
{
    /// <summary>Todoist's own order, with sub-tasks under the tasks they belong to.</summary>
    public static readonly TaskSort Default = new();

    public bool IsDefault => Column == TaskColumn.None;

    /// <summary>
    /// What clicking a column header asks for: the column already in use turns round, and any
    /// other starts again at the top of it.
    /// </summary>
    /// <remarks>
    /// Two states rather than three. Cycling round to the account's own order on a third click is
    /// tempting, but nothing else on Windows behaves that way, and a click that undoes the sort
    /// entirely is a surprise — the View menu is where the way back lives.
    /// </remarks>
    public TaskSort Clicked(TaskColumn column) => column switch
    {
        TaskColumn.None => Default,
        _ when column == Column => this with { Descending = !Descending },
        _ => new TaskSort(column),
    };
}
