namespace Termyn.Presentation;

/// <summary>
/// Where a selection in the outline goes when it lands on a day's heading.
/// </summary>
/// <remarks>
/// A heading is a row like any other, and every arrow key and every click can land on one — but
/// it has no task behind it, so the selection mustn't stay there. Which way it moves on is only
/// index arithmetic over the rows, and the same whatever draws them.
/// </remarks>
public static class OutlineSelection
{
    /// <summary>
    /// The row to carry a selection on to, having landed on a heading.
    /// </summary>
    /// <remarks>
    /// The way it was already going, so an arrow key keeps its direction and a click on a heading
    /// takes the day it heads. Turned round at either end, where carrying on would mean leaving
    /// the list — the top of Upcoming is a heading, and arriving there from below has to land on
    /// something.
    /// </remarks>
    /// <param name="rows">The outline's rows, headings and all</param>
    /// <param name="index">The heading the selection landed on</param>
    /// <param name="lastOnTask">The last row the selection settled on that was a task, or -1 for none</param>
    /// <returns>The row to take instead, or null when there's no task either way</returns>
    public static int? PastHeading(IReadOnlyList<TaskRow> rows, int index, int lastOnTask)
    {
        var forwards = index > lastOnTask;

        return Task(index, forwards ? 1 : -1) ?? Task(index, forwards ? -1 : 1);

        int? Task(int from, int step)
        {
            for (var at = from + step; at >= 0 && at < rows.Count; at += step)
                if (!rows[at].IsHeading)
                    return at;

            return null;
        }
    }
}
