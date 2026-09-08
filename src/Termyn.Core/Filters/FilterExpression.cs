using Termyn.Core.Model;

namespace Termyn.Core.Filters;

/// <summary>Which side of a day a term asks about.</summary>
public enum DayBound
{
    On,
    Before,
    After,
}

/// <summary>A day something other than the query itself names.</summary>
public enum DayAnchor
{
    /// <summary>The query says which day it means.</summary>
    None,

    /// <summary>The day the account calls "next week", which is a setting rather than a date.</summary>
    NextWeek,

    /// <summary>The first of the coming month, which Todoist writes <c>first day</c>.</summary>
    FirstOfMonth,
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
/// <param name="Anchor">What names the day, when the query doesn't name it itself</param>
public sealed record FilterDay(
    DateOnly? Absolute,
    int DaysFromToday,
    DayOfWeek? Weekday = null,
    DayAnchor Anchor = DayAnchor.None)
{
    public static FilterDay Today { get; } = new(null, 0);

    public static FilterDay On(DateOnly date) => new(date, 0);

    public static FilterDay FromToday(int days) => new(null, days);

    /// <summary>A day named by its place in the week, which is the third way Todoist writes one.</summary>
    public static FilterDay OnWeekday(DayOfWeek day) => new(null, 0, day);

    /// <summary>
    /// The day the account calls "next week", optionally some whole weeks further on.
    /// </summary>
    /// <remarks>
    /// The weeks are how Todoist writes the far end of a week-long window — "1 week after next
    /// week" — which is the only way to say "next week and no further" in its grammar.
    /// </remarks>
    /// <param name="weeksAfter">How many whole weeks past it, for the <c>N weeks after</c> form</param>
    /// <returns>The day</returns>
    public static FilterDay NextWeek(int weeksAfter = 0) => new(null, weeksAfter * 7, null, DayAnchor.NextWeek);

    /// <summary>The first of the coming month, which is what bounds "this calendar month".</summary>
    public static FilterDay FirstOfMonth { get; } = new(null, 0, null, DayAnchor.FirstOfMonth);

    /// <summary>
    /// The day this names, given what today is.
    /// </summary>
    /// <remarks>
    /// Null only when the day is the account's own "next week" and the account hasn't said which day
    /// that is. The caller refuses such a filter rather than running it, so this is the second line
    /// of the same defence: no day at all beats a day picked for the account by this client.
    /// </remarks>
    /// <param name="today">Today in the account's timezone</param>
    /// <param name="nextWeek">The day the account calls "next week", when it has said</param>
    /// <returns>The day itself, or null when it can't be worked out</returns>
    public DateOnly? Resolve(DateOnly today, DayOfWeek? nextWeek = null)
    {
        if (Absolute is { } date)
            return date;

        // The coming occurrence, counting today when it already matches — the same reading quick
        // add gives a weekday, so "sat" means one day in this app rather than two.
        if (Weekday is { } weekday)
            return today.AddDays(((int)weekday - (int)today.DayOfWeek + 7) % 7);

        // Always the next one, never today: putting something off until next week has to move it,
        // and a window from "next week" to a week later would otherwise be this week on that day.
        if (Anchor is DayAnchor.NextWeek)
            return nextWeek is not { } start
                ? null
                : today.AddDays(((int)start - (int)today.DayOfWeek + 6) % 7 + 1).AddDays(DaysFromToday);

        // The coming first, never this month's: "before first day" means the whole of this calendar
        // month, and on the first of it that has to still be true.
        if (Anchor is DayAnchor.FirstOfMonth)
            return new DateOnly(today.Year, today.Month, 1).AddMonths(1);

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
    /// What a filter has to know about the account before it can be answered.
    /// </summary>
    /// <remarks>
    /// Both of these come off the <c>user</c> resource, and neither is knowable before it has
    /// synced or if its shape ever drifts. "assigned to: me" with no "me" to compare against would
    /// quietly match nothing, and an empty list reads as an answer — "you have none" — rather than
    /// as the question it couldn't ask. So the caller checks this and refuses the filter instead,
    /// the same as one whose grammar it can't read.
    /// </remarks>
    /// <param name="expression">The parsed filter</param>
    /// <returns>Everything in the account the filter would need</returns>
    public static AccountFacts Needs(FilterExpression expression) => expression switch
    {
        AssignedToMe or AssignedToOthers or AssignedByMe or AddedByMe => AccountFacts.UserId,
        Due e => Needs(e.Day),
        Deadline e => Needs(e.Day),
        Created e => Needs(e.Day),
        Not e => Needs(e.Operand),
        And e => Needs(e.Left) | Needs(e.Right),
        Or e => Needs(e.Left) | Needs(e.Right),
        _ => AccountFacts.None,
    };

    private static AccountFacts Needs(FilterDay day)
        => day.Anchor is DayAnchor.NextWeek ? AccountFacts.NextWeek : AccountFacts.None;
}

/// <summary>What a filter needs to know about the account before it means anything.</summary>
[Flags]
public enum AccountFacts
{
    None = 0,

    /// <summary>Who the account belongs to, which is what <c>me</c> stands for.</summary>
    UserId = 1,

    /// <summary>The day the account calls "next week".</summary>
    NextWeek = 2,
}
