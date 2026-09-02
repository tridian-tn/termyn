namespace Termyn.Presentation;

/// <summary>
/// A saved filter Termyn can't evaluate, and what the notice over the empty list needs to say so.
/// </summary>
/// <remarks>
/// The link is worked out here rather than in the window because it is about the filter and not
/// about the notice: the window is handed somewhere to go, not the parts to build it from.
/// </remarks>
/// <param name="Query">The query, cut down to something a notice can hold on a line or two</param>
/// <param name="Link">Where to open this filter in Todoist, which can read what Termyn can't</param>
public sealed record UnreadableFilter(string Query, string Link);
