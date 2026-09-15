using Termyn.Core.Model;

namespace Termyn.Presentation;

/// <summary>
/// Somewhere a task can be moved to: a project, or a section inside one.
/// </summary>
/// <param name="Kind">Whether it's a project or a section</param>
/// <param name="Id">The project's or the section's own id</param>
/// <param name="Name">What it's called on its own</param>
/// <param name="Path">What it's called with everything above it, outermost first</param>
/// <param name="Depth">How far below the top level it sits, nought for a top-level project</param>
/// <param name="Here">Whether the task is already top level here, so moving it here would do nothing</param>
public sealed record MoveDestination(
    SidebarKind Kind,
    string Id,
    string Name,
    string Path,
    int Depth,
    bool Here = false);

/// <summary>
/// Where a task can go, and which of those places match what's been typed.
/// </summary>
public static class MoveDestinations
{
    /// <summary>What goes between the steps of a path.</summary>
    public const string Separator = " / ";

    /// <summary>
    /// The projects and sections the sidebar lists, in its order, each with the path to it.
    /// </summary>
    /// <remarks>
    /// Read off the sidebar rather than the model, the way the palette is, so the picker offers
    /// exactly what the tree does — the same names in the same order, and nothing archived. A
    /// favourited project is in the sidebar twice and here once.
    ///
    /// The path is rebuilt from the depths as the rows go by. The sidebar is already flattened
    /// depth-first, so whatever sits above a row at a shallower depth is what it's filed under.
    /// </remarks>
    /// <param name="sidebar">The sidebar as the presenter last built it</param>
    /// <param name="task">The task about to move, or null when the account doesn't hold it</param>
    /// <param name="inboxProjectId">The account's Inbox, where a task naming no project is</param>
    /// <returns>Every project and section, outermost first</returns>
    public static IReadOnlyList<MoveDestination> From(
        IReadOnlyList<SidebarNode> sidebar,
        TaskItem? task,
        string? inboxProjectId = null)
    {
        var destinations = new List<MoveDestination>();
        var above = new List<string>();

        foreach (var node in sidebar)
        {
            if (node.Kind is not (SidebarKind.Project or SidebarKind.Section))
                continue;

            if (node.Key != SidebarKeys.For(node.Kind, node.Id))
                continue;

            // Top-level projects sit at one in the sidebar, under the heading that holds them.
            var depth = Math.Max(0, node.Depth - 1);
            while (above.Count > depth)
                above.RemoveAt(above.Count - 1);

            var path = string.Join(Separator, above.Append(node.Label));
            destinations.Add(new MoveDestination(node.Kind, node.Id, node.Label, path, depth, IsHere(task, node, inboxProjectId)));

            above.Add(node.Label);
        }

        return destinations;
    }

    /// <summary>
    /// Whether a task is already top level in a project or section.
    /// </summary>
    /// <remarks>
    /// Top level and not merely inside. A sub-task moved to the section it's already in comes out
    /// from under its parent, which is a move worth making — so nowhere is "here" for one of those.
    ///
    /// A task naming no project is in the Inbox. One captured without a project has none until the
    /// server gives it the Inbox's, and the engine turns a move there away all the same.
    /// </remarks>
    private static bool IsHere(TaskItem? task, SidebarNode node, string? inboxProjectId)
        => task is { ParentId: null } && node.Kind switch
        {
            SidebarKind.Section => task.SectionId == node.Id,
            SidebarKind.Project => task.SectionId is null && (task.ProjectId ?? inboxProjectId) == node.Id,
            _ => false,
        };

    /// <summary>
    /// The destinations that match what's been typed, best first.
    /// </summary>
    /// <remarks>
    /// Matched against the name and the whole path both, taking the better. The path is what lets
    /// "work adm" find the Admin section in Work rather than every Admin there is, and the name is
    /// what stops a deeply filed section losing to a shallow one for being a longer string.
    ///
    /// Ties keep the sidebar's order, so the same letters give the same list every time.
    /// </remarks>
    /// <param name="destinations">Everywhere the task could go, in the sidebar's order</param>
    /// <param name="query">What's been typed</param>
    /// <returns>All of them in order when nothing has been typed, otherwise only the matches</returns>
    public static IReadOnlyList<MoveDestination> Rank(IReadOnlyList<MoveDestination> destinations, string? query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return destinations;

        return destinations
            .Select((destination, at) => (Destination: destination, At: at, Score: Score(destination, trimmed)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.At)
            .Select(x => x.Destination)
            .ToList();
    }

    /// <summary>How well a destination matches, or null when it doesn't.</summary>
    /// <remarks>
    /// The name ends the path, so anything that matches the name matches the path as well — which
    /// is why the path alone decides whether it's a match at all.
    /// </remarks>
    private static int? Score(MoveDestination destination, string query)
        => Fuzzy.Score(destination.Path, query) is { } path
            ? Math.Max(path, Fuzzy.Score(destination.Name, query) ?? path)
            : null;
}
