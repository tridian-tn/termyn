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
    /// Tokens the offline parser recognised but cannot act on — recurrence, reminders and
    /// assignees. The capture UI surfaces these so the user knows what was left out.
    /// </summary>
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    /// <summary>
    /// Whether the text describes a repeating schedule. Only the server resolves one, and a caller
    /// must not act on <see cref="DueDate"/> when this is set: "every day at 9am" is a schedule,
    /// and the nine o'clock in it is not a due time of its own.
    /// </summary>
    public bool IsRecurrence { get; init; }
}
