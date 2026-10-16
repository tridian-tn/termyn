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
    NewSubtask,
    Due,
    Deadline,
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
    MoveTo,
    ShowInTodoist,
    Delete,

    // ---- On the selected sidebar row ----
    RenameSelection,
    DeleteSelection,
    ToggleFavourite,
    MoveSelectionUp,
    MoveSelectionDown,

    // ---- Anywhere ----
    NewTask,
    NewProject,
    NewSection,
    QuickAdd,
    SyncNow,
    ToggleCompleted,
    History,
    SortDefault,
    ExpandAll,
    CollapseAll,
    ToggleDescription,
    ViewDescription,
    ViewComments,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    Undo,
    Search,
    Palette,
    PreviousView,
    NextView,
    Settings,
    CheckForUpdates,
    About,
    SignOut,
    Exit,
}

/// <summary>
/// What a task can be made to do from where it currently sits, so a menu can grey out what would
/// only fail. Worked out by the engine, which owns the ordering these answers come from.
/// </summary>
/// <param name="CanMoveTo">
/// Whether it can be sent to another project or section. False for a completed task fetched out of
/// the archive, which the account no longer holds anywhere a move could reach
/// </param>
/// <param name="CanShowInTodoist">
/// Whether Todoist has a page for it. False for a task made here that a sync hasn't taken to the
/// server yet, which Todoist has never heard of
/// </param>
public sealed record TaskAbilities(
    bool CanIndent = false,
    bool CanOutdent = false,
    bool CanMoveUp = false,
    bool CanMoveDown = false,
    bool CanMoveTo = false,
    bool CanShowInTodoist = false)
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
/// <param name="Zoomed">Whether that panel is scaled away from the size it rests at.</param>
/// <param name="NewTaskText">
/// What a new task started from the open view would begin with, or null when the view has nothing
/// to begin one with
/// </param>
public sealed record CommandContext(
    TaskRow? Task = null,
    TaskAbilities? Abilities = null,
    SidebarNode? Selection = null,
    bool ShowingCompleted = false,
    bool CanUndo = false,
    TaskSort? Sort = null,
    bool ShowingDescription = false,
    bool WritingDescription = false,
    bool ShowingComments = false,
    bool Zoomed = false,
    bool CanExpandAll = false,
    bool CanCollapseAll = false,
    TaskAbilities? SelectionAbilities = null,
    string? NewTaskText = null)
{
    /// <summary>Nothing selected anywhere — what a menu opened over an empty window would see.</summary>
    public static readonly CommandContext Empty = new();

    /// <summary>What the task can do, or nothing at all when there is no task.</summary>
    public TaskAbilities Can => Task is null ? TaskAbilities.None : Abilities ?? TaskAbilities.None;

    /// <summary>
    /// What the sidebar row can do, or nothing at all when there is no row.
    /// </summary>
    /// <remarks>
    /// The same record the outline uses, since the question is the same one — is there anywhere
    /// left to go in that direction — and only two of its four answers apply here.
    /// </remarks>
    public TaskAbilities SelectionCan
        => Selection is null ? TaskAbilities.None : SelectionAbilities ?? TaskAbilities.None;
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
/// Every surface reads from here: the menu bar, the task menu, and the palette. The wording itself
/// is in the catalogue. Labels are kept free of menu mnemonics so the palette can show them as they
/// are — the menus put the ampersands on their own headings, which are theirs and not shared.
/// </remarks>
public static class Commands
{
    /// <summary>The commands that act on whichever task the outline is on.</summary>
    public static bool IsTaskCommand(AppCommand command)
        => command is >= AppCommand.ToggleComplete and <= AppCommand.Delete;

    /// <summary>
    /// How deep sub-tasks may go, which is where the New sub-task entry stops being offered.
    /// </summary>
    /// <remarks>
    /// Todoist takes four levels below a top-level task, so a task at that depth can hold nothing
    /// further. The engine holds the same number for the indent path and refuses a move that would
    /// pass it; this is the half that greys the entry rather than letting it be picked and fail.
    /// </remarks>
    private const int MaxSubtaskDepth = 4;

    /// <summary>The commands that act on whichever row the sidebar is on.</summary>
    public static bool IsSelectionCommand(AppCommand command)
        => command is >= AppCommand.RenameSelection and <= AppCommand.MoveSelectionDown;

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
            AppCommand.ToggleComplete => Task(task is { Completed: true } ? Strings.CommandReopen : Strings.CommandComplete),

            // No ellipsis: the editor opens on the row itself rather than in a dialog.
            AppCommand.Rename => Task(Strings.CommandRename),

            AppCommand.Due => Task(Strings.CommandDue),
            AppCommand.Deadline => Task(Strings.CommandDeadline),
            AppCommand.Labels => Task(Strings.CommandLabels),

            // Left offered on a plan without reminders: the dialog is where the user can see what
            // they'd be getting, and it refuses the save itself rather than being absent here.
            AppCommand.Reminders => Task(Strings.CommandReminders),

            AppCommand.Priority1 or AppCommand.Priority2 or AppCommand.Priority3 or AppCommand.Priority4
                => new CommandState(PriorityLabel(command), task is not null, task?.Priority == PriorityOf(command)),

            AppCommand.Indent => new CommandState(Strings.CommandIndent, can.CanIndent),
            AppCommand.Outdent => new CommandState(Strings.CommandOutdent, can.CanOutdent),
            AppCommand.MoveUp => new CommandState(Strings.CommandMoveUp, can.CanMoveUp),
            AppCommand.MoveDown => new CommandState(Strings.CommandMoveDown, can.CanMoveDown),
            AppCommand.MoveTo => new CommandState(Strings.CommandMoveTo, can.CanMoveTo),

            // No ellipsis: nothing opens here to ask anything, the browser just goes to the task.
            AppCommand.ShowInTodoist => new CommandState(Strings.CommandShowInTodoist, can.CanShowInTodoist),

            AppCommand.Delete => Task(Strings.CommandDelete),

            // Not on a task that is finished with — a sub-task of one would be work filed under
            // something already ticked off, where nothing on screen would ever show it as due — and
            // not on one already as deep as Todoist will hold.
            AppCommand.NewSubtask => new CommandState(
                Strings.CommandNewSubtask,
                context.Task is { Completed: false, Depth: < MaxSubtaskDepth }),

            // Named for what is selected, because "Rename" over a sidebar holding projects,
            // sections and labels doesn't say which of the three is about to change.
            AppCommand.RenameSelection => Selection(
                Strings.CommandRenameProject,
                Strings.CommandRenameSection,
                Strings.CommandRenameLabel),

            AppCommand.DeleteSelection => Selection(
                Strings.CommandDeleteProject,
                Strings.CommandDeleteSection,
                Strings.CommandDeleteLabel),

            AppCommand.ToggleFavourite => Favourite(context.Selection),

            // Named for the thing rather than "Move up", which over a sidebar holding six kinds of
            // row doesn't say what is about to shift. Greyed at the ends of the list, and over
            // anything that isn't a project or a section, the same as the two above.
            AppCommand.MoveSelectionUp => new CommandState(
                Structure(Strings.CommandMoveProjectUp, Strings.CommandMoveSectionUp),
                context.SelectionCan.CanMoveUp),

            AppCommand.MoveSelectionDown => new CommandState(
                Structure(Strings.CommandMoveProjectDown, Strings.CommandMoveSectionDown),
                context.SelectionCan.CanMoveDown),

            // Greyed where the view has nothing to start a task with — Upcoming, All, a saved filter
            // — which Quick add, starting from nothing, is still there for.
            AppCommand.NewTask => new CommandState(Strings.CommandNewTask, context.NewTaskText is not null),
            AppCommand.NewProject => Always(Strings.CommandNewProject),

            // A section belongs to a project, so there has to be one under the sidebar to put it in.
            AppCommand.NewSection => new CommandState(
                Strings.CommandNewSection,
                context.Selection?.Kind is SidebarKind.Project),

            AppCommand.QuickAdd => Always(Strings.CommandQuickAdd),
            AppCommand.SyncNow => Always(Strings.CommandSyncNow),

            // Always offered, empty or not: a window that says "nothing yet" answers the question,
            // and a greyed entry leaves the user wondering what would fill it.
            AppCommand.History => Always(Strings.CommandHistory),
            AppCommand.ToggleCompleted => new CommandState(
                Strings.CommandCompletedTasks,
                true,
                context.ShowingCompleted),
            // Folding the whole view at once, which the expander on a row does one task at a time.
            // Each is greyed when there is nothing left for it to do, which is also how the pair
            // says which way the outline currently stands.
            AppCommand.ExpandAll => new CommandState(Strings.CommandExpandAll, context.CanExpandAll),
            AppCommand.CollapseAll => new CommandState(Strings.CommandCollapseAll, context.CanCollapseAll),

            // The way back from a sorted outline. Greyed when it is already in the account's own
            // order, which is also how the entry says which of the two you are looking at.
            AppCommand.SortDefault => new CommandState(
                Strings.CommandDefaultOrder,
                !(context.Sort ?? TaskSort.Default).IsDefault),

            // One name, with the tick saying whether the panel is open. An entry that renames itself
            // is a different entry each time you look, and the tick is already there to say it.
            //
            // "Details" and not "Description": the panel carries the comments as well now, on a tab
            // of their own, and nothing narrower would still be true of it.
            AppCommand.ToggleDescription => new CommandState(
                Strings.CommandDetails,
                true,
                context.ShowingDescription),

            // The two tabs the panel holds, each named for what it puts in front. Neither is a
            // toggle and neither carries a tick: they say where to go rather than what is on.
            //
            // Greyed on the tab already in front, which is the rule every other entry here is
            // greyed by — running it now would do nothing. A shut panel greys neither, since from
            // there both have somewhere to take you.
            AppCommand.ViewDescription => new CommandState(
                Strings.CommandViewDescription,
                !context.ShowingDescription || context.ShowingComments),

            AppCommand.ViewComments => new CommandState(
                Strings.CommandViewComments,
                !context.ShowingDescription || !context.ShowingComments),

            // The description panel already zooms to the wheel; these are the same thing said out
            // loud, for anybody who doesn't know the wheel does it. Offered on the same terms as
            // editing, because the comments the pane also shows aren't text it can scale.
            AppCommand.ZoomIn => new CommandState(Strings.CommandZoomIn, Scalable(context)),
            AppCommand.ZoomOut => new CommandState(Strings.CommandZoomOut, Scalable(context)),

            // The way back, greyed while there is nothing to come back from — which is also how the
            // entry says whether the panel is at its own size, the same as Default order above.
            AppCommand.ZoomReset => new CommandState(Strings.CommandZoomReset, Scalable(context) && context.Zoomed),

            AppCommand.Undo => new CommandState(Strings.CommandUndo, context.CanUndo),
            AppCommand.Search => Always(Strings.CommandSearch),
            AppCommand.Palette => Always(Strings.CommandPalette),
            AppCommand.PreviousView => Always(Strings.CommandPreviousView),
            AppCommand.NextView => Always(Strings.CommandNextView),
            AppCommand.Settings => Always(Strings.CommandSettings),
            AppCommand.CheckForUpdates => Always(Strings.CommandCheckForUpdates),
            AppCommand.About => Always(Strings.CommandAbout),

            // The ellipsis is for the question it asks first, about what signing out takes with it.
            AppCommand.SignOut => Always(Strings.CommandSignOut),

            AppCommand.Exit => Always(Strings.CommandExit),

            _ => new CommandState(string.Empty, false),
        };

        CommandState Task(string label) => new(label, task is not null);

        CommandState Always(string label) => new(label, true);

        // The panel has something in it that can be scaled: the description, either way round. The
        // comments it also shows are drawn rather than set in text, so there is nothing to scale.
        static bool Scalable(CommandContext at) => at.ShowingDescription && !at.ShowingComments;

        // The two kinds that have an order of their own. A label is sorted by the account and a
        // smart view is in a list nobody chose, so neither is something to move.
        string Structure(string project, string section)
            => context.Selection?.Kind is SidebarKind.Section ? section : project;

        // Only the three kinds the sidebar lets you rename or delete; a smart view or a saved
        // filter is not ours to change from here.
        //
        // Greyed over one of those, and named for a project while it is. "Rename item" left the
        // reader to work out which item, and over Today the answer was none — so the greyed entry
        // read as a thing that should have worked. Naming the commonest of the three says what the
        // entry is for at the same time as saying it can't be used on this.
        //
        // A whole phrase for each kind rather than one with the noun dropped in, since a language
        // that changes the noun's ending or article with the verb couldn't translate the pattern.
        CommandState Selection(string project, string section, string label) => context.Selection?.Kind switch
        {
            SidebarKind.Project => new(project, true),
            SidebarKind.Section => new(section, true),
            SidebarKind.Label => new(label, true),
            _ => new(project, false),
        };
    }

    /// <summary>
    /// The favourite toggle, which says which way it would go. Only projects and labels: a section
    /// has no star of its own, and a saved filter's belongs to the account rather than to us.
    /// </summary>
    private static CommandState Favourite(SidebarNode? node)
    {
        var eligible = node?.Kind is SidebarKind.Project or SidebarKind.Label;
        return new CommandState(
            node is { IsFavorite: true } ? Strings.CommandRemoveFromFavourites : Strings.CommandAddToFavourites,
            eligible);
    }

    private static string PriorityLabel(AppCommand command) => command switch
    {
        AppCommand.Priority1 => Strings.CommandPriority1,
        AppCommand.Priority2 => Strings.CommandPriority2,
        AppCommand.Priority3 => Strings.CommandPriority3,
        _ => Strings.CommandPriority4,
    };
}
