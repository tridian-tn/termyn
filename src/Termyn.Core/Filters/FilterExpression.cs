using Termyn.Core.Model;

namespace Termyn.Core.Filters;

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

    /// <summary>Substring of the task's content.</summary>
    public sealed record Search(string Text) : FilterExpression;

    public sealed record Not(FilterExpression Operand) : FilterExpression;

    public sealed record And(FilterExpression Left, FilterExpression Right) : FilterExpression;

    public sealed record Or(FilterExpression Left, FilterExpression Right) : FilterExpression;
}
