namespace Termyn.Core.Model;

/// <summary>Todoist sync resource-type names, and the set Termyn reads.</summary>
public static class ResourceType
{
    public const string Items = "items";
    public const string Projects = "projects";
    public const string Sections = "sections";
    public const string Labels = "labels";
    public const string Filters = "filters";
    public const string Reminders = "reminders";
    public const string User = "user";

    /// <summary>Resource types fetched on every sync. Notes/comments are excluded (out of v1 scope).</summary>
    public static readonly IReadOnlyList<string> All = [Items, Projects, Sections, Labels, Filters, Reminders, User];
}
