using Termyn.Core.Model;

namespace Termyn.Core.Filters;

/// <summary>Which side of a day a term asks about.</summary>
public enum DayBound
{
    On,
    Before,
    After,
}

/// <summary>
/// A day a filter term names: a date outright, or a number of days either side of today.
/// </summary>
/// <remarks>
/// Kept relative rather than worked out while parsing. A query is parsed once and evaluated for as
/// long as the view is open, and "created: today" resolved at parse time would go on meaning the
/// day the app was started — which for a window left up overnight is the wrong day by morning.
/// </remarks>
/// <param name="Absolute">The day named outright, or null when it is counted from today</param>
/// <param name="DaysFromToday">How many days either side of today, when it is counted</param>
/// <param name="Weekday">The day of the week named, when it is named that way</param>
public sealed record FilterDay(DateOnly? Absolute, int DaysFromToday, DayOfWeek? Weekday = null)
{
    public static FilterDay Today { get; } = new(null, 0);

    public static FilterDay On(DateOnly date) => new(date, 0);

    public static FilterDay FromToday(int days) => new(null, days);

    /// <summary>A day named by its place in the week, which is the third way Todoist writes one.</summary>
    public static FilterDay OnWeekday(DayOfWeek day) => new(null, 0, day);

    /// <summary>The day this names, given what today is.</summary>
    /// <param name="today">Today in the account's timezone</param>
    /// <returns>The day itself</returns>
    public DateOnly Resolve(DateOnly today)
    {
        if (Absolute is { } date)
            return date;

        // The coming occurrence, counting today when it already matches — the same reading quick
        // add gives a weekday, so "sat" means one day in this app rather than two.
        if (Weekday is { } weekday)
            return today.AddDays(((int)weekday - (int)today.DayOfWeek + 7) % 7);

        return today.AddDays(DaysFromToday);
    }
}

/// <summary>
/// A parsed filter query. The grammar Termyn evaluates locally is a deliberate subset of Todoist's:
/// anything outside it is reported as unsupported rather than approximated, because a filter that
/// quietly returns the wrong tasks is worse than one that admits it can't be answered here.
/// </summary>
public abstract record FilterExpression
{
    private FilterExpression()
    {
    }

    /// <summary>
    /// Every task there is. Todoist writes this <c>view all</c>, and gives every account a saved
    /// filter of that name.
    /// </summary>
    public sealed record Everything : FilterExpression;

    /// <summary>Tasks in a named project, optionally including everything beneath it.</summary>
    public sealed record InProject(string Name, bool IncludeSubProjects) : FilterExpression;

    /// <summary>
    /// Tasks in a named section, which Todoist writes <c>/name</c>.
    /// </summary>
    /// <remarks>
    /// The name isn't tied to a project: every section of that name counts, wherever it is, and
    /// narrowing to one project is what <c>#Work &amp; /Meetings</c> is for.
    /// </remarks>
    public sealed record InSection(string Name) : FilterExpression;

    public sealed record HasLabel(string Name) : FilterExpression;

    /// <summary>Tasks carrying no labels at all.</summary>
    public sealed record NoLabels : FilterExpression;

    public sealed record HasPriority(Priority Priority) : FilterExpression;

    /// <summary>Due today only. Overdue is separate, unlike the Today smart view.</summary>
    public sealed record DueToday : FilterExpression;

    /// <summary>Due strictly before today.</summary>
    public sealed record Overdue : FilterExpression;

    public sealed record NoDate : FilterExpression;

    /// <summary>Due within <paramref name="Days"/> days, counting today.</summary>
    public sealed record NextDays(int Days) : FilterExpression;

    /// <summary>Due within the <paramref name="Days"/> days up to now, counting today.</summary>
    /// <remarks>The mirror of <see cref="NextDays"/>, which is how Todoist's <c>-30 days</c> reads.</remarks>
    public sealed record LastDays(int Days) : FilterExpression;

    /// <summary>Due on, before or after a given day — Todoist's <c>due:</c> and <c>date:</c>.</summary>
    public sealed record Due(DayBound Bound, FilterDay Day) : FilterExpression;

    /// <summary>The day it has to be finished by, which Todoist writes <c>deadline:</c>.</summary>
    public sealed record Deadline(DayBound Bound, FilterDay Day) : FilterExpression;

    public sealed record NoDeadline : FilterExpression;

    /// <summary>Who a task is for, or who put it there.</summary>
    /// <remarks>
    /// Only ever about the account itself. Naming anyone else would mean holding the collaborators,
    /// which this client doesn't sync — so "assigned to: Sam" is refused whole rather than answered
    /// with a guess about which Sam.
    /// </remarks>
    public sealed record AssignedToMe : FilterExpression;

    /// <summary>Assigned, and to somebody who isn't you.</summary>
    public sealed record AssignedToOthers : FilterExpression;

    /// <summary>Assigned to anybody at all.</summary>
    public sealed record Assigned : FilterExpression;

    public sealed record AssignedByMe : FilterExpression;

    public sealed record AddedByMe : FilterExpression;

    /// <summary>In a project somebody else can see, which is what makes assignment mean anything.</summary>
    public sealed record Shared : FilterExpression;

    /// <summary>Tasks whose date names no hour. A task with no date at all names no hour either.</summary>
    public sealed record NoTime : FilterExpression;

    /// <summary>Tasks that come round again rather than finishing.</summary>
    public sealed record Recurring : FilterExpression;

    /// <summary>Tasks filed under another task.</summary>
    public sealed record Subtask : FilterExpression;

    /// <summary>When the task was added, which Todoist writes <c>created:</c>.</summary>
    public sealed record Created(DayBound Bound, FilterDay Day) : FilterExpression;

    /// <summary>Substring of the task's content.</summary>
    public sealed record Search(string Text) : FilterExpression;

    public sealed record Not(FilterExpression Operand) : FilterExpression;

    public sealed record And(FilterExpression Left, FilterExpression Right) : FilterExpression;

    public sealed record Or(FilterExpression Left, FilterExpression Right) : FilterExpression;

    /// <summary>
    /// Whether anything in here asks who the account is.
    /// </summary>
    /// <remarks>
    /// Which matters before the user resource has synced, or if its shape ever drifts: "assigned to:
    /// me" with no "me" to compare against would quietly match nothing, and an empty list reads as
    /// an answer — "you have none" — rather than as the question it couldn't ask. So the caller
    /// checks this and refuses the filter instead, the same as one whose grammar it can't read.
    /// </remarks>
    /// <param name="expression">The parsed filter</param>
    /// <returns>Whether any term in it names the account</returns>
    public static bool NamesTheAccount(FilterExpression expression) => expression switch
    {
        AssignedToMe or AssignedToOthers or AssignedByMe or AddedByMe => true,
        Not e => NamesTheAccount(e.Operand),
        And e => NamesTheAccount(e.Left) || NamesTheAccount(e.Right),
        Or e => NamesTheAccount(e.Left) || NamesTheAccount(e.Right),
        _ => false,
    };
}
