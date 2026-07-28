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

/// <summary>
/// A label. Tasks carry labels by <em>name</em>, not by id, so the name is the join key and a
/// rename has to be pushed through every task that wore the old one.
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
