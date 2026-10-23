using Termyn.Core.Model;

namespace Termyn.Core.Capture;

/// <summary>The result of parsing quick-add text locally.</summary>
public sealed record QuickAddParse
{
    /// <summary>The task text with every recognised token removed.</summary>
    public required string Content { get; init; }

    public string? ProjectName { get; init; }
    public string? SectionName { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public Priority Priority { get; init; } = Priority.P4;
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }

    /// <summary>
    /// Tokens the offline parser recognised but cannot act on — recurrence, assignees, and a
    /// reminder it couldn't read (a recurring <c>!every</c>, or a form it hasn't the grammar for).
    /// The capture UI surfaces these so the user knows what was left out. A reminder it could read
    /// is in <see cref="Reminders"/> instead, not here.
    /// </summary>
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    /// <summary>
    /// The reminders the text's <c>!</c> shortcuts name, read the way Todoist's apps read them.
    /// Empty when none were typed, or when a <c>!</c> was flagged in <see cref="Unsupported"/>
    /// rather than read. The caller adds these onto the task once it has an id, and only when the
    /// plan takes reminders.
    /// </summary>
    public IReadOnlyList<CaptureReminder> Reminders { get; init; } = [];

    /// <summary>
    /// Whether the text describes a repeating schedule. Only the server resolves one, and a caller
    /// must not act on <see cref="DueDate"/> when this is set: "every day at 9am" is a schedule,
    /// and the nine o'clock in it is not a due time of its own.
    /// </summary>
    public bool IsRecurrence { get; init; }
}
