using Termyn.Core.Model;

namespace Termyn.Core.Filters;

/// <summary>
/// What a filter is evaluated against: the account's projects, and today's date in the account's
/// own timezone. Project name lookups are resolved once and reused, since a filter runs over every
/// task in the account.
/// </summary>
public sealed class FilterContext
{
    private readonly IReadOnlyList<Project> _projects;
    private readonly Dictionary<(string Name, bool IncludeSubProjects), HashSet<string>> _resolved = new();

    public FilterContext(IReadOnlyList<Project> projects, DateOnly today, TimeZoneInfo zone)
    {
        _projects = projects;
        Today = today;
        Zone = zone;
    }

    public DateOnly Today { get; }

    public TimeZoneInfo Zone { get; }

    /// <summary>
    /// The projects a <c>#name</c> refers to. Names aren't unique in Todoist, so every project of
    /// that name counts — matching only the first would drop tasks the user can see under the name.
    /// </summary>
    public HashSet<string> ProjectIds(string name, bool includeSubProjects)
    {
        if (_resolved.TryGetValue((name, includeSubProjects), out var cached))
            return cached;

        var ids = _projects
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (includeSubProjects)
        {
            bool grew;
            do
            {
                grew = false;
                foreach (var project in _projects)
                    if (project.ParentId is { } parent && ids.Contains(parent) && ids.Add(project.Id))
                        grew = true;
            }
            while (grew); // a project's children can be enumerated before the project itself
        }

        _resolved[(name, includeSubProjects)] = ids;
        return ids;
    }
}

/// <summary>Applies a parsed filter to a task.</summary>
public static class FilterEvaluator
{
    public static bool Matches(FilterExpression expression, TaskItem item, FilterContext context) => expression switch
    {
        FilterExpression.InProject e =>
            item.ProjectId is { } id && context.ProjectIds(e.Name, e.IncludeSubProjects).Contains(id),

        FilterExpression.HasLabel e =>
            item.Labels.Contains(e.Name, StringComparer.OrdinalIgnoreCase),

        FilterExpression.HasPriority e => item.Priority == e.Priority,

        // Today only. The Today smart view sweeps in overdue as well, but as a filter term the two
        // are separate — "today | overdue" is how you ask for both.
        FilterExpression.DueToday => SmartViews.DueOn(item, context.Zone) == context.Today,

        FilterExpression.Overdue => SmartViews.DueOn(item, context.Zone) is { } due && due < context.Today,

        // Read off the raw field, not the parsed date: a due date Termyn can't read is still a date.
        FilterExpression.NoDate => item.DueDate is null,

        // N days counting today, so "next 7 days" ends six days out. Overdue isn't in the window.
        FilterExpression.NextDays e =>
            SmartViews.DueOn(item, context.Zone) is { } day
            && day >= context.Today
            && day <= context.Today.AddDays(e.Days - 1),

        FilterExpression.Search e => item.Content.Contains(e.Text, StringComparison.OrdinalIgnoreCase),

        FilterExpression.Not e => !Matches(e.Operand, item, context),

        FilterExpression.And e => Matches(e.Left, item, context) && Matches(e.Right, item, context),

        FilterExpression.Or e => Matches(e.Left, item, context) || Matches(e.Right, item, context),

        _ => true,
    };
}
