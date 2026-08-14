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

    /// <summary>Comments on a task. Todoist calls them notes; everything above the API says comments.</summary>
    public const string Notes = "notes";

    /// <summary>
    /// Comments on a project. A separate resource type from <see cref="Notes"/>, though both are
    /// written with the same <c>note_*</c> commands.
    /// </summary>
    public const string ProjectNotes = "project_notes";

    /// <summary>What the account's plan allows — reminders among it. Arrives as a single object.</summary>
    public const string UserPlanLimits = "user_plan_limits";

    /// <summary>Resource types fetched on every sync.</summary>
    public static readonly IReadOnlyList<string> All =
        [Items, Projects, Sections, Labels, Filters, Reminders, Notes, ProjectNotes, User, UserPlanLimits];

    /// <summary>Whether a resource type holds comments, whichever thing they hang off.</summary>
    public static bool IsComments(string type)
        => type == Notes || type == ProjectNotes;
}
