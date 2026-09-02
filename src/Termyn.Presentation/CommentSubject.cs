namespace Termyn.Presentation;

/// <summary>
/// What the detail panel is about: whose comments it would show, and what to call them.
/// </summary>
/// <remarks>
/// The two together, because they are one decision. Worked out separately they would be free to
/// disagree, and a panel headed with a project's name showing a task's conversation is a worse lie
/// than either half alone.
///
/// Named for the comments because they are what turns on it — the description follows the task on
/// its own and has nothing to say about a project — but the name is what the whole panel is headed
/// with, both tabs included.
/// </remarks>
/// <param name="Id">The task or the project the comments hang off, or null when neither is picked out</param>
/// <param name="About">What to head the panel with, or empty when there is nothing to head it about</param>
public sealed record CommentSubject(string? Id, string About)
{
    /// <summary>Neither a task nor a project: a label or a filter with nothing selected in it.</summary>
    public static readonly CommentSubject None = new(null, string.Empty);

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
    public static CommentSubject Of(TaskRow? task, SidebarNode? selection) => (task, selection) switch
    {
        ({ } row, _) => new CommentSubject(row.Id, $"Task: {row.Content}"),
        (_, { Kind: SidebarKind.Project } project) => new CommentSubject(project.Id, $"Project: {project.Label}"),
        _ => None,
    };
}
