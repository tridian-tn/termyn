using Termyn.Core.Model;

namespace Termyn.Presentation;

public enum SidebarKind
{
    SmartView,
    Project,
    Section,
}

/// <summary>One row of the sidebar tree, already flattened with its indent depth.</summary>
public sealed record SidebarNode(
    SidebarKind Kind,
    string Id,
    string Label,
    int Depth,
    bool IsFavorite = false,
    SmartView? View = null,
    int Count = 0);

/// <summary>What the outline is currently showing.</summary>
public sealed record ViewSelection(SmartView? View = SmartView.Today, string? ProjectId = null, string? SectionId = null)
{
    public static readonly ViewSelection Default = new();

    public static ViewSelection Of(SmartView view) => new(view);

    public static ViewSelection OfProject(string projectId) => new(null, projectId);

    public static ViewSelection OfSection(string sectionId) => new(null, null, sectionId);

    /// <summary>The sidebar row this selection corresponds to.</summary>
    public string Key => View?.ToString() ?? ProjectId ?? SectionId ?? string.Empty;
}
