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
