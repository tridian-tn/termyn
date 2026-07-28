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
/// Builds the keys that identify sidebar rows. Todoist ids are only unique within a resource type,
/// so the kind is part of the key: without it a project and a filter that happen to share an id
/// would be the same row as far as the tree is concerned.
/// </summary>
public static class SidebarKeys
{
    public static string For(SidebarKind kind, string id) => kind switch
    {
        SidebarKind.SmartView => id,
        SidebarKind.Project => "project:" + id,
        SidebarKind.Section => "section:" + id,
        SidebarKind.Label => "label:" + id,
        SidebarKind.Filter => "filter:" + id,
        _ => "header:" + id,
    };

    /// <summary>The same row as it appears under Favourites, which is a row of its own.</summary>
    public static string Favourite(SidebarKind kind, string id) => "favourite:" + For(kind, id);
}

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
    public string Key => this switch
    {
        { View: { } view } => SidebarKeys.For(SidebarKind.SmartView, view.ToString()),
        { ProjectId: { } project } => SidebarKeys.For(SidebarKind.Project, project),
        { SectionId: { } section } => SidebarKeys.For(SidebarKind.Section, section),
        { LabelName: { } label } => SidebarKeys.For(SidebarKind.Label, label),
        { FilterId: { } filter } => SidebarKeys.For(SidebarKind.Filter, filter),
        _ => string.Empty,
    };
}
