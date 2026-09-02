namespace Termyn.Presentation;

/// <summary>What the detail panel is about, when it is about anything.</summary>
public enum SubjectKind
{
    /// <summary>Neither a task nor a project — a label or a filter with nothing selected in it.</summary>
    None,

    Task,
    Project,
}

/// <summary>
/// What the detail panel is about: whose description and comments it shows, and what to call them.
/// </summary>
/// <remarks>
/// The three together, because they are one decision. Worked out separately they would be free to
/// disagree, and a panel headed with a project's name showing a task's conversation is a worse lie
/// than either half alone.
///
/// The kind is not decoration. Todoist ids are only unique within a resource type, so a project and
/// a task can share one — which means nothing downstream can work out from the id alone which of the
/// two it has, and writing to the wrong one is a silent, wrong edit rather than an error.
/// </remarks>
/// <param name="Kind">Whether this is a task, a project, or nothing</param>
/// <param name="Id">The task or the project, or null when neither is picked out</param>
/// <param name="About">What to head the panel with, or empty when there is nothing to head it about</param>
public sealed record PanelSubject(SubjectKind Kind, string? Id, string About)
{
    /// <summary>Nothing the panel can show: a label or a filter with no task selected in it.</summary>
    public static readonly PanelSubject None = new(SubjectKind.None, null, string.Empty);

    /// <summary>
    /// Reads the subject off the selection.
    /// </summary>
    /// <remarks>
    /// A task wins over the project holding it, because picking one out of the list is asking about
    /// that one. Drop back to the project and you are asking about the project — which is the whole
    /// of it: a project's comments used to need a menu entry of their own to reach, and now they are
    /// just what you get when you have not narrowed things down to a task.
    /// </remarks>
    /// <param name="task">The row the outline is on, or null when it is on none</param>
    /// <param name="selection">The row the sidebar is on, or null when it is on none</param>
    /// <returns>What the panel is about, and what to call it</returns>
    public static PanelSubject Of(TaskRow? task, SidebarNode? selection) => (task, selection) switch
    {
        ({ } row, _) => new PanelSubject(SubjectKind.Task, row.Id, $"Task: {row.Content}"),
        (_, { Kind: SidebarKind.Project } project)
            => new PanelSubject(SubjectKind.Project, project.Id, $"Project: {project.Label}"),
        _ => None,
    };
}
