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
    private readonly IReadOnlyList<Section> _sections;
    private readonly Dictionary<(string Name, bool IncludeSubProjects), HashSet<string>> _resolved = new();
    private readonly Dictionary<string, HashSet<string>> _resolvedSections = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _shared;

    public FilterContext(
        IReadOnlyList<Project> projects,
        DateOnly today,
        TimeZoneInfo zone,
        IReadOnlyList<Section>? sections = null,
        string? userId = null,
        DayOfWeek? nextWeek = null)
    {
        _projects = projects;
        _sections = sections ?? [];
        Today = today;
        Zone = zone;
        UserId = userId;
        NextWeek = nextWeek;
    }

    public DateOnly Today { get; }

    public TimeZoneInfo Zone { get; }

    /// <summary>
    /// Who the account belongs to, as Todoist's user id — the "me" the assignment terms name.
    /// </summary>
    /// <remarks>
    /// Null until the user resource has synced. A filter that names the account is refused before it
    /// gets this far, so terms needing it match nothing here rather than falling back on a guess.
    /// </remarks>
    public string? UserId { get; }

    /// <summary>
    /// The day the account calls "next week" — the one Todoist puts a task off until.
    /// </summary>
    /// <remarks>
    /// Null until the user resource has synced, and no default stands in: which day it is decides
    /// which week a filter answers about, and picking one for the account would be a week's worth
    /// of wrong tasks presented as the right ones. A filter needing it is refused before it gets
    /// this far.
    /// </remarks>
    public DayOfWeek? NextWeek { get; }

    /// <summary>
    /// Whether a project is one somebody else can see.
    /// </summary>
    /// <remarks>
    /// The server's word, not worked out from the collaborators — which this client doesn't sync.
    /// </remarks>
    /// <param name="projectId">The project the task is filed in</param>
    /// <returns>Whether it's shared</returns>
    public bool IsShared(string projectId)
    {
        _shared ??= _projects.Where(p => p.IsShared).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return _shared.Contains(projectId);
    }

    /// <summary>
    /// The projects a <c>#name</c> refers to. Names aren't unique in Todoist, so every project of
    /// that name counts — matching only the first would drop tasks the user can see under the name.
    /// </summary>
    public HashSet<string> ProjectIds(string name, bool includeSubProjects)
    {
        if (_resolved.TryGetValue((name, includeSubProjects), out var cached))
            return cached;

        var named = _projects
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id);

        var ids = includeSubProjects
            ? ProjectTree.WithDescendants(_projects, named)
            : named.ToHashSet(StringComparer.Ordinal);

        _resolved[(name, includeSubProjects)] = ids;
        return ids;
    }

    /// <summary>
    /// The sections a <c>/name</c> refers to.
    /// </summary>
    /// <remarks>
    /// Every section of that name, in every project. Section names repeat far more than project
    /// names do — half an account's projects can have a "Later" — and a filter naming one means all
    /// of them, which is why narrowing it down is what the project term is for.
    /// </remarks>
    /// <param name="name">The name as the query wrote it</param>
    /// <returns>The ids of every section called that</returns>
    public HashSet<string> SectionIds(string name)
    {
        if (_resolvedSections.TryGetValue(name, out var cached))
            return cached;

        var ids = _sections
            .Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        _resolvedSections[name] = ids;
        return ids;
    }
}

/// <summary>Applies a parsed filter to a task.</summary>
public static class FilterEvaluator
{
    public static bool Matches(FilterExpression expression, TaskItem item, FilterContext context) => expression switch
    {
        FilterExpression.Everything => true,

        FilterExpression.InProject e =>
            item.ProjectId is { } id && context.ProjectIds(e.Name, e.IncludeSubProjects).Contains(id),

        FilterExpression.InSection e =>
            item.SectionId is { } id && context.SectionIds(e.Name).Contains(id),

        FilterExpression.HasLabel e =>
            item.Labels.Contains(e.Name, StringComparer.OrdinalIgnoreCase),

        FilterExpression.NoLabels => item.Labels.Count == 0,

        FilterExpression.HasPriority e => item.Priority == e.Priority,

        FilterExpression.Recurring => item.IsRecurring,

        FilterExpression.Subtask => item.ParentId is not null,

        // Today only. The Today smart view sweeps in overdue as well, but as a filter term the two
        // are separate — "today | overdue" is how you ask for both.
        FilterExpression.DueToday => SmartViews.DueOn(item, context.Zone) == context.Today,

        FilterExpression.Overdue => SmartViews.DueOn(item, context.Zone) is { } due && due < context.Today,

        // Read off the raw field, not the parsed date: a due date Termyn can't read is still a date.
        FilterExpression.NoDate => item.DueDate is null,

        FilterExpression.NoDeadline => item.Deadline is null,

        // A task with no date at all has no time of day either, and Todoist's own documented way of
        // asking for "a date and a time" is "!no date & !no time" — which needs both halves only
        // because this one lets the dateless through.
        FilterExpression.NoTime => !SmartViews.DueHasTime(item),

        // N days counting today, so "next 7 days" ends six days out. Overdue isn't in the window.
        FilterExpression.NextDays e =>
            SmartViews.DueOn(item, context.Zone) is { } day
            && day >= context.Today
            && day <= context.Today.AddDays(e.Days - 1),

        // The same window the other way. Both count today, which is the day they share.
        FilterExpression.LastDays e =>
            SmartViews.DueOn(item, context.Zone) is { } day
            && day <= context.Today
            && day >= context.Today.AddDays(-(e.Days - 1)),

        FilterExpression.Due e => On(SmartViews.DueOn(item, context.Zone), e.Bound, e.Day, context),

        FilterExpression.Deadline e => On(SmartViews.DeadlineOn(item, context.Zone), e.Bound, e.Day, context),

        FilterExpression.Created e => On(SmartViews.AddedOn(item, context.Zone), e.Bound, e.Day, context),

        FilterExpression.Search e => item.Content.Contains(e.Text, StringComparison.OrdinalIgnoreCase),

        // Assigned to anybody at all, which is the term "!assigned" turns into the tasks nobody owns.
        FilterExpression.Assigned => item.ResponsibleUid is not null,

        FilterExpression.AssignedToMe => context.UserId is { } me && item.ResponsibleUid == me,

        // Assigned, and to somebody else. An unassigned task isn't assigned to others, so both
        // halves have to hold — and without a "me" to compare against, neither can be told apart.
        FilterExpression.AssignedToOthers =>
            item.ResponsibleUid is { } holder && context.UserId is { } me && holder != me,

        FilterExpression.AssignedByMe => context.UserId is { } me && item.AssignedByUid == me,

        FilterExpression.AddedByMe => context.UserId is { } me && item.AddedByUid == me,

        FilterExpression.Shared => item.ProjectId is { } project && context.IsShared(project),

        FilterExpression.Not e => !Matches(e.Operand, item, context),

        FilterExpression.And e => Matches(e.Left, item, context) && Matches(e.Right, item, context),

        FilterExpression.Or e => Matches(e.Left, item, context) || Matches(e.Right, item, context),

        // Unreachable while every case is handled, and false rather than true so that it stays
        // harmless if one ever isn't: a term nobody evaluates should match nothing, not everything.
        _ => false,
    };

    /// <summary>
    /// Whether a day the task carries falls the named side of the day a term asks about.
    /// </summary>
    /// <remarks>
    /// A task that hasn't got the day at all matches none of these rather than all of them: the
    /// question is when something happens, and "never" isn't an answer to it. Asking the other way
    /// round — for the tasks without one — is what the <c>no …</c> terms are for.
    /// </remarks>
    /// <param name="day">The day the task carries, or null when it hasn't got one</param>
    /// <param name="bound">Which side of the term's day counts</param>
    /// <param name="wanted">The day the term names</param>
    /// <param name="context">The account the days are worked out against</param>
    /// <returns>Whether the task's day answers the term</returns>
    private static bool On(DateOnly? day, DayBound bound, FilterDay wanted, FilterContext context)
    {
        if (day is not { } had)
            return false;

        // A day the account itself names — "next week" — is unknowable until the account has
        // synced, and nothing it hasn't said can be answered.
        if (wanted.Resolve(context.Today, context.NextWeek) is not { } mark)
            return false;

        return bound switch
        {
            DayBound.Before => had < mark,
            DayBound.After => had > mark,
            _ => had == mark,
        };
    }
}
