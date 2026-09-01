using Termyn.Core.Model;

namespace Termyn.Presentation;

/// <summary>
/// Everything the app can be asked to do. One enum rather than one per surface: the menu bar, the
/// right-click menu, the command palette and the keyboard all name the same actions, and three
/// spellings of "complete this task" is three chances for them to mean different things.
/// </summary>
public enum AppCommand
{
    /// <summary>Nothing of its own — an entry that only holds the ones beneath it.</summary>
    None,

    // ---- On the selected task ----
    ToggleComplete,
    Rename,
    Due,
    Priority1,
    Priority2,
    Priority3,
    Priority4,
    Labels,
    Reminders,
    Indent,
    Outdent,
    MoveUp,
    MoveDown,
    Delete,

    // ---- On the selected sidebar row ----
    RenameSelection,
    DeleteSelection,
    ToggleFavourite,
    CommentOnProject,

    // ---- Anywhere ----
    NewTask,
    NewProject,
    NewSection,
    QuickAdd,
    SyncNow,
    ToggleCompleted,
    SortDefault,
    ToggleDescription,
    EditDescription,
    ToggleComments,
    Undo,
    Search,
    Palette,
    PreviousView,
    NextView,
    Settings,
    CheckForUpdates,
    About,
    Exit,
}

/// <summary>
/// What a task can be made to do from where it currently sits, so a menu can grey out what would
/// only fail. Worked out by the engine, which owns the ordering these answers come from.
/// </summary>
public sealed record TaskAbilities(
    bool CanIndent = false,
    bool CanOutdent = false,
    bool CanMoveUp = false,
    bool CanMoveDown = false)
{
    /// <summary>No task in hand, so nothing is on offer.</summary>
    public static readonly TaskAbilities None = new();
}

/// <summary>
/// What the app has in hand when a menu opens. Everything a command's label or availability can
/// turn on, gathered once so a menu of thirty entries asks thirty questions of one snapshot rather
/// than of the model.
/// </summary>
/// <param name="Task">The task the outline is on, or null when it is on none.</param>
/// <param name="Abilities">What that task can be made to do.</param>
/// <param name="Selection">The sidebar row that is selected, or null.</param>
/// <param name="ShowingCompleted">Whether the outline is also showing completed tasks.</param>
/// <param name="CanUndo">Whether there is anything to take back.</param>
/// <param name="Sort">How the outline is currently ordered.</param>
/// <param name="ShowingDescription">Whether the description panel is open under the outline.</param>
/// <param name="WritingDescription">Whether that panel is showing the markdown rather than the rendering.</param>
/// <param name="ShowingComments">Whether that panel is showing the comments rather than the description.</param>
public sealed record CommandContext(
    TaskRow? Task = null,
    TaskAbilities? Abilities = null,
    SidebarNode? Selection = null,
    bool ShowingCompleted = false,
    bool CanUndo = false,
    TaskSort? Sort = null,
    bool ShowingDescription = false,
    bool WritingDescription = false,
    bool ShowingComments = false)
{
    /// <summary>Nothing selected anywhere — what a menu opened over an empty window would see.</summary>
    public static readonly CommandContext Empty = new();

    /// <summary>What the task can do, or nothing at all when there is no task.</summary>
    public TaskAbilities Can => Task is null ? TaskAbilities.None : Abilities ?? TaskAbilities.None;
}

/// <summary>
/// How a command should currently be shown.
/// </summary>
/// <remarks>
/// A command that can be on or off says so through <see cref="Checked"/> and never by renaming
/// itself. The two are not interchangeable, and which one a surface leans on is the surface's own
/// business: a menu draws a tick beside the entry, the palette marks the row. Wording the state into
/// the label instead would make an entry read as a different entry each time you looked, and would
/// mean every surface had to be told separately how to spell it.
///
/// So a new toggle needs nothing of either surface — it sets <see cref="Checked"/> and both already
/// know what to do with it. A label that changes with what is <em>selected</em> is a different thing
/// and still fine: "Rename project" against "Rename label" names its object, not its state.
/// </remarks>
/// <param name="Label">What to call it, which some commands change with the thing they act on</param>
/// <param name="Enabled">False when running it now would do nothing</param>
/// <param name="Checked">Whether it is currently on, for the surface to show however it can</param>
public sealed record CommandState(string Label, bool Enabled, bool Checked = false);

/// <summary>
/// The one place that says what each command is called and whether it can be run.
/// </summary>
/// <remarks>
/// Every surface reads from here: the menu bar, the task menu, and the palette. Labels are kept
/// free of menu mnemonics so the palette can show them as they are — the menus put the ampersands
/// on their own headings, which are theirs and not shared.
/// </remarks>
public static class Commands
{
    /// <summary>The commands that act on whichever task the outline is on.</summary>
    public static bool IsTaskCommand(AppCommand command)
        => command is >= AppCommand.ToggleComplete and <= AppCommand.Delete;

    /// <summary>The commands that act on whichever row the sidebar is on.</summary>
    public static bool IsSelectionCommand(AppCommand command)
        => command is >= AppCommand.RenameSelection and <= AppCommand.ToggleFavourite;

    /// <summary>The priority a command sets, or null when it sets none.</summary>
    public static Priority? PriorityOf(AppCommand command) => command switch
    {
        AppCommand.Priority1 => Priority.P1,
        AppCommand.Priority2 => Priority.P2,
        AppCommand.Priority3 => Priority.P3,
        AppCommand.Priority4 => Priority.P4,
        _ => null,
    };

    /// <summary>How a command should be shown, given what is currently selected.</summary>
    public static CommandState StateOf(AppCommand command, CommandContext context)
    {
        var task = context.Task;
        var can = context.Can;

        return command switch
        {
            // Ticking off a task that is already done means putting it back, so the one action
            // wears the label the row has earned.
            AppCommand.ToggleComplete => Task(task is { Completed: true } ? "Reopen" : "Complete"),

            // No ellipsis: the editor opens on the row itself rather than in a dialog.
            AppCommand.Rename => Task("Rename"),

            AppCommand.Due => Task("Due date…"),
            AppCommand.Labels => Task("Labels…"),

            // Left offered on a plan without reminders: the dialog is where the user can see what
            // they'd be getting, and it refuses the save itself rather than being absent here.
            AppCommand.Reminders => Task("Reminders…"),

            AppCommand.Priority1 or AppCommand.Priority2 or AppCommand.Priority3 or AppCommand.Priority4
                => new CommandState(PriorityLabel(command), task is not null, task?.Priority == PriorityOf(command)),

            AppCommand.Indent => new CommandState("Indent", can.CanIndent),
            AppCommand.Outdent => new CommandState("Outdent", can.CanOutdent),
            AppCommand.MoveUp => new CommandState("Move up", can.CanMoveUp),
            AppCommand.MoveDown => new CommandState("Move down", can.CanMoveDown),
            AppCommand.Delete => Task("Delete"),

            // Named for what is selected, because "Rename" over a sidebar holding projects,
            // sections and labels doesn't say which of the three is about to change.
            AppCommand.RenameSelection => Selection("Rename {0}"),
            AppCommand.DeleteSelection => Selection("Delete {0}"),
            AppCommand.ToggleFavourite => Favourite(context.Selection),

            // A project's own comments, which nothing in the outline can reach: the pane follows the
            // task you are on, and a project is never one of those.
            AppCommand.CommentOnProject => new CommandState(
                "Comments on project",
                context.Selection?.Kind is SidebarKind.Project),

            AppCommand.NewTask => Always("New task"),
            AppCommand.NewProject => Always("New project"),

            // A section belongs to a project, so there has to be one under the sidebar to put it in.
            AppCommand.NewSection => new CommandState(
                "New section",
                context.Selection?.Kind is SidebarKind.Project),

            AppCommand.QuickAdd => Always("Quick add…"),
            AppCommand.SyncNow => Always("Sync now"),
            AppCommand.ToggleCompleted => new CommandState(
                "Completed tasks",
                true,
                context.ShowingCompleted),
            // The way back from a sorted outline. Greyed when it is already in the account's own
            // order, which is also how the entry says which of the two you are looking at.
            AppCommand.SortDefault => new CommandState(
                "Default order",
                !(context.Sort ?? TaskSort.Default).IsDefault),

            // One name, with the tick saying whether the panel is open. An entry that renames itself
            // is a different entry each time you look, and the tick is already there to say it.
            AppCommand.ToggleDescription => new CommandState(
                "Description",
                true,
                context.ShowingDescription),

            // Only worth offering while the panel it belongs to is open. Ticked while the markdown
            // is on show, since that is the state you leave rather than the one the panel rests in
            // — and ticked is all it says, the name standing still like every other entry's.
            AppCommand.EditDescription => new CommandState(
                "Edit description",
                context.ShowingDescription && !context.ShowingComments,
                context.WritingDescription),

            // The same pane, showing a third thing. Ticked while the comments are the thing it is
            // showing, so the entry says which of the two you are looking at.
            AppCommand.ToggleComments => new CommandState(
                "Comments",
                true,
                context.ShowingComments),

            AppCommand.Undo => new CommandState("Undo", context.CanUndo),
            AppCommand.Search => Always("Search…"),
            AppCommand.Palette => Always("Command palette…"),
            AppCommand.PreviousView => Always("Previous view"),
            AppCommand.NextView => Always("Next view"),
            AppCommand.Settings => Always("Settings…"),
            AppCommand.CheckForUpdates => Always("Check for updates…"),
            AppCommand.About => Always("About Termyn"),
            AppCommand.Exit => Always("Exit"),

            _ => new CommandState(string.Empty, false),
        };

        CommandState Task(string label) => new(label, task is not null);

        CommandState Always(string label) => new(label, true);

        // Only the three kinds the sidebar lets you rename or delete; a smart view or a saved
        // filter is not ours to change from here.
        CommandState Selection(string format)
        {
            var kind = NameOf(context.Selection?.Kind);
            return new CommandState(
                string.Format(format, kind ?? "item"),
                kind is not null);
        }
    }

    /// <summary>What a sidebar row is called in a sentence, or null when it can't be edited here.</summary>
    private static string? NameOf(SidebarKind? kind) => kind switch
    {
        SidebarKind.Project => "project",
        SidebarKind.Section => "section",
        SidebarKind.Label => "label",
        _ => null,
    };

    /// <summary>
    /// The favourite toggle, which says which way it would go. Only projects and labels: a section
    /// has no star of its own, and a saved filter's belongs to the account rather than to us.
    /// </summary>
    private static CommandState Favourite(SidebarNode? node)
    {
        var eligible = node?.Kind is SidebarKind.Project or SidebarKind.Label;
        return new CommandState(
            node is { IsFavorite: true } ? "Remove from favourites" : "Add to favourites",
            eligible);
    }

    private static string PriorityLabel(AppCommand command) => command switch
    {
        AppCommand.Priority1 => "Priority 1 — urgent",
        AppCommand.Priority2 => "Priority 2 — high",
        AppCommand.Priority3 => "Priority 3 — medium",
        _ => "Priority 4 — none",
    };
}
