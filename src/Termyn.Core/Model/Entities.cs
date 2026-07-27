namespace Termyn.Core.Model;

/// <summary>A Todoist task, reduced to the fields the list needs today.</summary>
public sealed class TaskItem
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public string? ProjectId { get; init; }
    public string? SectionId { get; init; }
    public string? ParentId { get; init; }
    public int ChildOrder { get; init; }
    public Priority Priority { get; init; } = Priority.P4;
    public IReadOnlyList<string> Labels { get; init; } = [];
    public bool Completed { get; init; }
    public string? DueDate { get; init; }
    public string? DueText { get; init; }
}

/// <summary>A Todoist project.</summary>
public sealed class Project
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ParentId { get; init; }
    public bool IsInboxProject { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsArchived { get; init; }
    public int ChildOrder { get; init; }
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
