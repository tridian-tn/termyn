namespace Termyn.Core.Model;

/// <summary>A Todoist task, reduced to the fields the list needs today.</summary>
public sealed class TaskItem
{
    public required string Id { get; init; }
    public required string Content { get; init; }

    /// <summary>
    /// The description under the task. Markdown, as the account stores it — Todoist's own editor
    /// is a rich-text skin over the same text, so what arrives here is the source and not a rendering
    /// of it.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    public string? ProjectId { get; init; }
    public string? SectionId { get; init; }
    public string? ParentId { get; init; }
    public int ChildOrder { get; init; }
    public Priority Priority { get; init; } = Priority.P4;
    public IReadOnlyList<string> Labels { get; init; } = [];
    public bool Completed { get; init; }

    /// <summary>When it was ticked off, as the server wrote it. Null while the task is still active.</summary>
    public string? CompletedAt { get; init; }

    /// <summary>When the task was created, as the server wrote it — a UTC instant.</summary>
    public string? AddedAt { get; init; }

    public string? DueDate { get; init; }

    /// <summary>The schedule as it was written — "every Monday" — which is what a recurrence is.</summary>
    public string? DueText { get; init; }

    /// <summary>
    /// Whether closing this task advances it rather than finishing it. The server decides this from
    /// the due string; Termyn only reports what it is told.
    /// </summary>
    public bool IsRecurring { get; init; }
}

/// <summary>How a reminder is triggered.</summary>
public enum ReminderKind
{
    /// <summary>A number of minutes before the task falls due.</summary>
    Relative,

    /// <summary>A moment of its own, independent of the task's due date.</summary>
    Absolute,

    /// <summary>Arriving at or leaving a place. Shown but not edited — there is no map here.</summary>
    Location,

    /// <summary>
    /// Something Todoist has that this client doesn't know. Shown, never touched: a kind we can't
    /// describe is one we certainly can't recreate.
    /// </summary>
    Unknown,
}

/// <summary>A reminder on a task.</summary>
public sealed class Reminder
{
    public required string Id { get; init; }
    public string? ItemId { get; init; }
    public ReminderKind Kind { get; init; } = ReminderKind.Relative;

    /// <summary>Minutes before the due date, for a relative reminder.</summary>
    public int MinuteOffset { get; init; }

    /// <summary>When an absolute reminder fires, as the server wrote it.</summary>
    public string? DueDate { get; init; }

    /// <summary>The place a location reminder watches, for display.</summary>
    public string? LocationName { get; init; }
}

/// <summary>
/// What the account's plan allows. Only the parts Termyn gates on are read; the resource carries a
/// great deal more.
/// </summary>
public sealed class PlanLimits
{
    public string PlanName { get; init; } = string.Empty;

    /// <summary>Whether the account may set reminders at all. False on the free plan.</summary>
    public bool Reminders { get; init; }

    /// <summary>How many time-based reminders the plan allows.</summary>
    public int MaxTimeReminders { get; init; }

    /// <summary>
    /// The largest file the plan will take, in megabytes. Zero when the server hasn't said.
    /// </summary>
    /// <remarks>
    /// Not knowing is not the same as no limit, but it can't be treated the way an unknown reminder
    /// entitlement is either: refusing every upload because the plan hasn't arrived yet would make
    /// attachments unusable before the first sync lands. So an unknown limit lets the upload be
    /// attempted and lets the server be the one to refuse it.
    /// </remarks>
    public int UploadLimitMb { get; init; }

    /// <summary>
    /// Whether a file of this size is worth offering to upload.
    /// </summary>
    /// <param name="bytes">The file's size</param>
    /// <returns>False only when the plan states a limit and the file is over it</returns>
    public bool AllowsUploadOf(long bytes) => UploadLimitMb <= 0 || bytes <= (long)UploadLimitMb * 1024 * 1024;
}

/// <summary>A Todoist project.</summary>
public sealed class Project
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The description on the project. Markdown, the same as a task's.</summary>
    public string Description { get; init; } = string.Empty;

    public string? ParentId { get; init; }
    public bool IsInboxProject { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsArchived { get; init; }
    public int ChildOrder { get; init; }
}

/// <summary>Walks the project tree, which several callers need and none can assume is ordered.</summary>
public static class ProjectTree
{
    /// <summary>
    /// The given projects together with everything filed beneath them, however deep. Repeated until
    /// nothing new appears rather than walked once, because a child can be enumerated before its
    /// parent; the set doubles as the guard that stops a parent cycle looping forever.
    /// </summary>
    public static HashSet<string> WithDescendants(IEnumerable<Project> projects, IEnumerable<string> roots)
    {
        var all = projects.ToList();
        var found = roots.ToHashSet(StringComparer.Ordinal);

        bool grew;
        do
        {
            grew = false;
            foreach (var project in all)
                if (project.ParentId is { } parent && found.Contains(parent) && found.Add(project.Id))
                    grew = true;
        }
        while (grew);

        return found;
    }
}

/// <summary>A section within a project.</summary>
public sealed class Section
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ProjectId { get; init; }
    public bool IsArchived { get; init; }
    public int SectionOrder { get; init; }
}

/// <summary>
/// A label. Tasks carry labels by <em>name</em>, not by id, so the name is the join key — which is
/// why a rename is the server's to carry across to them, and Termyn takes its word for it on the
/// next sync rather than rewriting the tasks itself.
/// </summary>
public sealed class Label
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsFavorite { get; init; }
    public int ItemOrder { get; init; }
}

/// <summary>A saved filter: a stored query string, evaluated locally where the grammar allows.</summary>
public sealed class Filter
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Query { get; init; } = string.Empty;
    public bool IsFavorite { get; init; }
    public int ItemOrder { get; init; }
}

/// <summary>
/// A comment, on either a task or a project. Exactly one of <see cref="ItemId"/> and
/// <see cref="ProjectId"/> says which.
/// </summary>
public sealed class Comment
{
    public required string Id { get; init; }

    /// <summary>The task it's filed under, or null when it belongs to a project.</summary>
    public string? ItemId { get; init; }

    /// <summary>The project it's filed under, or null when it belongs to a task.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Whichever of the two it hangs off, so callers that don't care needn't ask twice.</summary>
    public string OwnerId => ItemId ?? ProjectId ?? string.Empty;

    /// <summary>The comment itself, as the markdown the account stores.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// When it was posted, as the server wrote it — an ISO-8601 instant, so it orders correctly as
    /// text and needs parsing only to be displayed. Null on one this client has only just queued.
    /// </summary>
    public string? PostedAt { get; init; }

    /// <summary>The file hanging off this comment, or null when there is none.</summary>
    public FileAttachment? Attachment { get; init; }
}

/// <summary>
/// A file hanging off a comment.
/// </summary>
/// <remarks>
/// Metadata only, which is all that ever syncs. The bytes are fetched on request and live in a
/// cache that can be swept at any time, so nothing here is a promise that the file is on this
/// machine — <see cref="FileUrl"/> is where it can always be got again.
/// </remarks>
/// <param name="FileName">What the file is called, as the account stores it</param>
/// <param name="FileSize">Its size in bytes, or zero when the server didn't say</param>
/// <param name="FileType">Its media type, or empty when the server didn't say</param>
/// <param name="FileUrl">Where to fetch it, and the handle a delete names</param>
/// <param name="Pending">Whether the server is still processing the upload</param>
public sealed record FileAttachment(
    string FileName,
    long FileSize,
    string FileType,
    string FileUrl,
    bool Pending)
{
    /// <summary>Whether there's anywhere to fetch this from. A pending upload has no url yet.</summary>
    public bool CanFetch => FileUrl.Length > 0;
}
