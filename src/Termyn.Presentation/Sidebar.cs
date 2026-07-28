using Termyn.Core.Model;

namespace Termyn.Presentation;

public enum SidebarKind
{
    SmartView,
    Project,
    Section,
    Label,
    Filter,

    /// <summary>A group label such as "Favourites". Not selectable.</summary>
    Header,
}

/// <summary>One row of the sidebar tree, already flattened with its indent depth.</summary>
/// <param name="Key">
/// Identifies this row uniquely. A favourited project appears twice — once under Favourites and
/// once in the tree — so the id alone can't tell the two rows apart.
/// </param>
public sealed record SidebarNode(
    SidebarKind Kind,
    string Id,
    string Label,
    int Depth,
    string Key,
    bool IsFavorite = false,
    SmartView? View = null,
    int Count = 0);

/// <summary>
/// What the outline is currently showing. A label is held by name rather than id, because that is
/// how tasks refer to labels.
/// </summary>
public sealed record ViewSelection(
    SmartView? View = SmartView.Today,
    string? ProjectId = null,
    string? SectionId = null,
    string? LabelName = null,
    string? FilterId = null)
{
    public static readonly ViewSelection Default = new();

    public static ViewSelection Of(SmartView view) => new(view);

    public static ViewSelection OfProject(string projectId) => new(null, projectId);

    public static ViewSelection OfSection(string sectionId) => new(null, null, sectionId);

    public static ViewSelection OfLabel(string labelName) => new(null, null, null, labelName);

    public static ViewSelection OfFilter(string filterId) => new(null, null, null, null, filterId);

    /// <summary>The sidebar row this selection corresponds to.</summary>
    public string Key
        => View?.ToString() ?? ProjectId ?? SectionId ?? (LabelName is { } l ? "label:" + l : null) ?? FilterId ?? string.Empty;
}
