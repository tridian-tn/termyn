namespace Termyn.Presentation;

/// <summary>Where a task dragged over the outline would be put down, relative to the task it's over.</summary>
public enum DropZone
{
    /// <summary>Immediately before it, among its siblings.</summary>
    Before,

    /// <summary>Under it, as its last sub-task.</summary>
    Onto,

    /// <summary>Immediately after it, among its siblings.</summary>
    After,
}

/// <summary>
/// A drop worked out against the rows on screen: what it would do, and where to show that it would.
/// </summary>
/// <param name="Id">The task the dragged one would be put down beside, or on</param>
/// <param name="Zone">Which of those it'd be</param>
/// <param name="Row">
/// The row the mark goes on: along its top edge before, along its bottom edge after, and round it
/// onto
/// </param>
/// <param name="Depth">How far in the mark starts, as an indent level, which is where the task would land</param>
public sealed record DropTarget(string Id, DropZone Zone, int Row, int Depth);

/// <summary>
/// What a drop over the outline would do, from where the pointer is over its rows.
/// </summary>
/// <remarks>
/// Only arithmetic over the rows, and the same whatever draws them. Whether the account will take
/// the drop is the engine's to say; this only says which drop the pointer is asking for.
/// </remarks>
public static class OutlineDrop
{
    /// <summary>
    /// Which part of a row a point is over.
    /// </summary>
    /// <remarks>
    /// Thirds: the top third of a row puts a task before it, the bottom third after it, and the
    /// middle onto it. Above and below the row count as its top and bottom.
    /// </remarks>
    /// <param name="y">How far down the row the pointer is, in pixels from its top</param>
    /// <param name="height">How tall the row is, in pixels</param>
    /// <returns>Where on that row a drop would go</returns>
    public static DropZone ZoneAt(int y, int height)
    {
        var third = height / 3;

        return y < third ? DropZone.Before
            : y >= height - third ? DropZone.After
            : DropZone.Onto;
    }

    /// <summary>
    /// The drop the pointer is asking for over a row.
    /// </summary>
    /// <remarks>
    /// An unfolded task's sub-tasks are drawn straight under it, so the gap below it is the gap above
    /// its first one. A task dropped there goes where the mark was drawn — first among them — rather
    /// than after the parent's whole subtree, a screenful away from where it was let go.
    /// </remarks>
    /// <param name="rows">The outline's rows</param>
    /// <param name="index">The row the pointer is over</param>
    /// <param name="zone">Which part of it, from <see cref="ZoneAt"/></param>
    /// <returns>The drop, or null when the row isn't a task to drop by</returns>
    public static DropTarget? Over(IReadOnlyList<TaskRow> rows, int index, DropZone zone)
    {
        if (index < 0 || index >= rows.Count || rows[index].IsHeading)
            return null;

        var row = rows[index];

        if (zone == DropZone.After && index + 1 < rows.Count && rows[index + 1] is { IsHeading: false } next && next.Depth > row.Depth)
            return new DropTarget(next.Id, DropZone.Before, index + 1, next.Depth);

        return new DropTarget(row.Id, zone, index, row.Depth);
    }

    /// <summary>
    /// The drop the pointer is asking for in the empty space below the last row.
    /// </summary>
    /// <remarks>
    /// The end of the list at its top level, which is the one place no row's own zones can reach
    /// when the last top-level task is unfolded: after it, its first sub-task takes the drop, and
    /// after its last sub-task is down at that sub-task's level. Finished tasks listed at the
    /// bottom don't count, since the order stops meaning anything once a task is done.
    /// </remarks>
    /// <param name="rows">The outline's rows</param>
    /// <returns>The drop after the last top-level task still to do, or null when there's none</returns>
    public static DropTarget? Below(IReadOnlyList<TaskRow> rows)
    {
        var root = -1;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i] is { Depth: 0, IsHeading: false, Completed: false })
                root = i;

        if (root < 0)
            return null;

        // Marked under the last of what's drawn beneath it, which is where it will land.
        var last = root;
        while (last + 1 < rows.Count && rows[last + 1].Depth > 0)
            last++;

        return new DropTarget(rows[root].Id, DropZone.After, last, 0);
    }

    /// <summary>
    /// Whether a drop would move the dragged task anywhere on screen.
    /// </summary>
    /// <remarks>
    /// Asked of the rows, because only they know what's shown. The engine turns away a drop that
    /// leaves the account's order as it was, but a section's view shows only some of the tasks that
    /// order runs through. Just above the next task shown there can still be below one from another
    /// section, and putting it there would reorder the project while this view stayed as it was.
    /// </remarks>
    /// <param name="rows">The outline's rows</param>
    /// <param name="id">The task being dragged</param>
    /// <param name="target">Where it would go</param>
    /// <returns>False when it's already the task shown on that side, at that level</returns>
    public static bool Moves(IReadOnlyList<TaskRow> rows, string id, DropTarget target)
    {
        if (target.Zone == DropZone.Onto)
            return true;

        var at = IndexOf(rows, target.Id);
        if (at < 0)
            return true;

        var beside = target.Zone == DropZone.Before ? Sibling(rows, at, -1) : Sibling(rows, at, 1);
        return beside < 0 || rows[beside].Id != id;
    }

    /// <summary>
    /// The nearest row at the same level as another, in one direction, without leaving its parent.
    /// </summary>
    /// <param name="rows">The outline's rows</param>
    /// <param name="at">The row to start from</param>
    /// <param name="step">Which way to look: -1 for up the list, 1 for down</param>
    /// <returns>That row's index, or -1 when the parent's run ends first</returns>
    private static int Sibling(IReadOnlyList<TaskRow> rows, int at, int step)
    {
        var depth = rows[at].Depth;

        for (var i = at + step; i >= 0 && i < rows.Count; i += step)
        {
            if (rows[i].Depth < depth)
                return -1;

            if (rows[i].Depth == depth)
                return i;
        }

        return -1;
    }

    private static int IndexOf(IReadOnlyList<TaskRow> rows, string id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id == id)
                return i;

        return -1;
    }
}
