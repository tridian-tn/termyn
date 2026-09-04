using Termyn.Core.Model;
using Termyn.Core.Sync;

namespace Termyn.Presentation;

/// <summary>
/// One step of the path to whatever the outline is showing.
/// </summary>
/// <param name="Label">What that step is called</param>
/// <param name="Target">
/// The view this step goes to, or null for the last one — the step you are already standing on,
/// which says where you are rather than offering somewhere to go
/// </param>
public sealed record Crumb(string Label, ViewSelection? Target);

/// <summary>A stretch of the written path that goes somewhere.</summary>
/// <param name="Start">Where it begins in the line</param>
/// <param name="Length">How many characters of it lead there</param>
/// <param name="Target">The view it goes to</param>
public sealed record PathLink(int Start, int Length, ViewSelection Target);

/// <summary>The path as one line, and the stretches of it that can be followed.</summary>
public sealed record PathLine(string Text, IReadOnlyList<PathLink> Links);

/// <summary>
/// The path to the current view, for a line above the outline saying which list you are looking at.
/// </summary>
/// <remarks>
/// The sidebar says this too, by highlighting a row — but a tree that has lost the focus draws its
/// selection faintly, and the answer to "which list is this?" shouldn't depend on where the focus
/// happens to be.
///
/// Every step but the last is a view in its own right, so the path doubles as the way back up: a
/// section names the project holding it, a nested project names the ones above it. A smart view, a
/// label and a filter have nothing above them and are a single step. So are search results, which
/// stand in for the path while a search is on: see <see cref="SearchResults"/>.
/// </remarks>
public static class ViewPath
{
    /// <summary>The path while a search is on: one step, leading nowhere.</summary>
    /// <remarks>
    /// A search runs over the whole account rather than the list that was open, so a path still
    /// naming that list would say the results came from it. And there's nothing above the results
    /// to go back up to — clearing the search box is the way back.
    /// </remarks>
    public static readonly IReadOnlyList<Crumb> SearchResults = [new Crumb("Search results", null)];

    /// <summary>
    /// The path written as one line, with where in it each step that leads somewhere sits.
    /// </summary>
    /// <remarks>
    /// The positions are counted as the line is built rather than searched for afterwards: two
    /// projects of one name in the same path is ordinary, and looking up a step by its text would
    /// find the first of them twice.
    /// </remarks>
    /// <param name="path">The steps, as <see cref="For"/> returned them</param>
    /// <param name="separator">What to put between them</param>
    /// <returns>The line and its links, which are in the order the steps are</returns>
    public static PathLine Line(IReadOnlyList<Crumb> path, string separator)
    {
        var links = new List<PathLink>();
        var at = 0;

        foreach (var crumb in path)
        {
            if (crumb.Target is { } target && crumb.Label.Length > 0)
                links.Add(new PathLink(at, crumb.Label.Length, target));

            at += crumb.Label.Length + separator.Length;
        }

        return new PathLine(string.Join(separator, path.Select(c => c.Label)), links);
    }

    /// <summary>The steps from the top down, the current view last.</summary>
    /// <param name="selection">What the outline is showing</param>
    /// <param name="snapshot">The model the names are read from</param>
    /// <returns>The path, or empty when the selection names nothing the model still holds</returns>
    public static List<Crumb> For(ViewSelection selection, ModelSnapshot snapshot)
    {
        var steps = new List<Crumb>();

        switch (selection)
        {
            case { View: { } view }:
                steps.Add(new Crumb(Named(view), ViewSelection.Of(view)));
                break;

            case { ProjectId: { } projectId }:
                steps.AddRange(Ancestry(projectId, snapshot));
                break;

            case { SectionId: { } sectionId }:
                if (snapshot.Sections.FirstOrDefault(s => s.Id == sectionId) is not { } section)
                    break;

                if (section.ProjectId is { } owner)
                    steps.AddRange(Ancestry(owner, snapshot));

                steps.Add(new Crumb(section.Name, ViewSelection.OfSection(section.Id)));
                break;

            // Checked against the account like the others, rather than taken on trust because the
            // selection carries the name rather than an id. A label deleted by a sync leaves the
            // name behind here, and a path naming one the account hasn't got is a path to nowhere.
            case { LabelName: { } label }:
                if (snapshot.Labels.Any(l => string.Equals(l.Name, label, StringComparison.OrdinalIgnoreCase)))
                    steps.Add(new Crumb(label, ViewSelection.OfLabel(label)));
                break;

            case { FilterId: { } filterId }:
                if (snapshot.Filters.FirstOrDefault(f => f.Id == filterId) is { } filter)
                    steps.Add(new Crumb(filter.Name, ViewSelection.OfFilter(filter.Id)));
                break;
        }

        // Where you already are, so it leads nowhere. Everything above it stays a way back up.
        if (steps.Count > 0)
            steps[^1] = steps[^1] with { Target = null };

        return steps;
    }

    /// <summary>A project and the ones holding it, outermost first.</summary>
    private static List<Crumb> Ancestry(string projectId, ModelSnapshot snapshot)
    {
        var byId = snapshot.Projects.DistinctBy(p => p.Id).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var chain = new List<Crumb>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // A parent cycle in the data would otherwise walk for ever.
        for (var at = projectId; at is not null && seen.Add(at);)
        {
            if (!byId.TryGetValue(at, out var project))
                break;

            chain.Add(new Crumb(project.Name, ViewSelection.OfProject(project.Id)));
            at = project.ParentId;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>The wording the sidebar uses for a built-in view, so the two agree.</summary>
    private static string Named(SmartView view) => view switch
    {
        SmartView.Today => "Today",
        SmartView.Upcoming => "Upcoming",
        SmartView.Inbox => "Inbox",
        _ => "All",
    };
}
