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
public sealed record FilterDay(DateOnly? Absolute, int DaysFromToday)
{
    public static FilterDay Today { get; } = new(null, 0);

    public static FilterDay On(DateOnly date) => new(date, 0);

    public static FilterDay FromToday(int days) => new(null, days);

    /// <summary>The day this names, given what today is.</summary>
    /// <param name="today">Today in the account's timezone</param>
    /// <returns>The day itself</returns>
    public DateOnly Resolve(DateOnly today) => Absolute ?? today.AddDays(DaysFromToday);
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

    public sealed record HasLabel(string Name) : FilterExpression;

    public sealed record HasPriority(Priority Priority) : FilterExpression;

    /// <summary>Due today only. Overdue is separate, unlike the Today smart view.</summary>
    public sealed record DueToday : FilterExpression;

    /// <summary>Due strictly before today.</summary>
    public sealed record Overdue : FilterExpression;

    public sealed record NoDate : FilterExpression;

    /// <summary>Due within <paramref name="Days"/> days, counting today.</summary>
    public sealed record NextDays(int Days) : FilterExpression;

    /// <summary>When the task was added, which Todoist writes <c>created:</c>.</summary>
    public sealed record Created(DayBound Bound, FilterDay Day) : FilterExpression;

    /// <summary>Substring of the task's content.</summary>
    public sealed record Search(string Text) : FilterExpression;

    public sealed record Not(FilterExpression Operand) : FilterExpression;

    public sealed record And(FilterExpression Left, FilterExpression Right) : FilterExpression;

    public sealed record Or(FilterExpression Left, FilterExpression Right) : FilterExpression;
}
