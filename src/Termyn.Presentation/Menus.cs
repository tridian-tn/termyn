namespace Termyn.Presentation;

/// <summary>
/// One entry of a menu. What it says and whether it can be run come from <see cref="Commands"/>;
/// all that is held here is the shape — what sits where, and what is ruled off from what.
/// </summary>
/// <param name="Command">What running it asks for, or None for a heading.</param>
/// <param name="Heading">
/// A heading's own text, which is the only wording a menu owns. Carries the mnemonic ampersand,
/// which is why it doesn't come from the catalogue: the palette shows the same commands and has no
/// use for one.
/// </param>
/// <param name="SeparatorBefore">Opens a new group, so related entries read together.</param>
/// <param name="Children">The entries beneath a heading.</param>
public sealed record MenuEntry(
    AppCommand Command,
    string? Heading = null,
    bool SeparatorBefore = false,
    IReadOnlyList<MenuEntry>? Children = null)
{
    /// <summary>A heading with entries under it, which does nothing itself.</summary>
    public static MenuEntry Group(string heading, params MenuEntry[] children)
        => new(AppCommand.None, heading, Children: children);

    /// <summary>An entry that runs a command.</summary>
    public static MenuEntry Of(AppCommand command) => new(command);

    /// <summary>An entry that runs a command, opening a new group.</summary>
    public static MenuEntry AfterRule(AppCommand command) => new(command, SeparatorBefore: true);
}

/// <summary>
/// Where every command sits — the menu bar, and the right-click menu on a task.
/// </summary>
/// <remarks>
/// The two share the task entries rather than listing them twice, so an action added to one is in
/// the other by the time it is written down. Framework-free, so the shape can be read in a test and
/// so a second platform builds the same menus from the same list.
/// </remarks>
public static class Menus
{
    /// <summary>
    /// What can be done to a task. The Task menu and the right-click menu are this same list.
    /// </summary>
    private static readonly MenuEntry[] TaskEntries =
    [
        MenuEntry.Of(AppCommand.ToggleComplete),
        MenuEntry.Of(AppCommand.Rename),

        MenuEntry.AfterRule(AppCommand.Due),
        MenuEntry.Group(
            "&Priority",
            MenuEntry.Of(AppCommand.Priority1),
            MenuEntry.Of(AppCommand.Priority2),
            MenuEntry.Of(AppCommand.Priority3),
            MenuEntry.Of(AppCommand.Priority4)),
        MenuEntry.Of(AppCommand.Labels),
        MenuEntry.Of(AppCommand.Reminders),

        MenuEntry.AfterRule(AppCommand.Indent),
        MenuEntry.Of(AppCommand.Outdent),
        MenuEntry.Of(AppCommand.MoveUp),
        MenuEntry.Of(AppCommand.MoveDown),

        // Its own group at the bottom, away from anything the user meant to click.
        MenuEntry.AfterRule(AppCommand.Delete),
    ];

    /// <summary>The right-click menu on a task.</summary>
    public static IReadOnlyList<MenuEntry> TaskContext => TaskEntries;

    /// <summary>The menu bar, in the order its headings are laid out.</summary>
    public static IReadOnlyList<MenuEntry> Bar { get; } =
    [
        MenuEntry.Group(
            "&File",
            MenuEntry.Of(AppCommand.NewTask),
            MenuEntry.Of(AppCommand.NewProject),
            MenuEntry.Of(AppCommand.NewSection),
            MenuEntry.AfterRule(AppCommand.QuickAdd),
            MenuEntry.AfterRule(AppCommand.SyncNow),
            MenuEntry.AfterRule(AppCommand.Settings),
            MenuEntry.AfterRule(AppCommand.Exit)),

        MenuEntry.Group(
            "&Edit",
            MenuEntry.Of(AppCommand.Undo),
            MenuEntry.AfterRule(AppCommand.Search),
            MenuEntry.Of(AppCommand.Palette)),

        MenuEntry.Group(
            "&View",
            MenuEntry.Of(AppCommand.PreviousView),
            MenuEntry.Of(AppCommand.NextView),
            MenuEntry.AfterRule(AppCommand.ToggleCompleted),
            MenuEntry.AfterRule(AppCommand.ToggleDescription),
            MenuEntry.Of(AppCommand.EditDescription),
            MenuEntry.Of(AppCommand.ToggleComments),

            // The description panel's own size, which the wheel has always changed and nothing has
            // ever said so.
            MenuEntry.AfterRule(AppCommand.ZoomIn),
            MenuEntry.Of(AppCommand.ZoomOut),
            MenuEntry.Of(AppCommand.ZoomReset),

            // Sorting is done by clicking a column header; this is the way back from it, and the
            // only part of it that needs somewhere to live.
            MenuEntry.AfterRule(AppCommand.SortDefault)),

        // The same entries the right-click menu shows, so neither can gain an action the other
        // hasn't got.
        new MenuEntry(AppCommand.None, "&Task", Children: TaskEntries),

        // Everything the sidebar's own keys do, which had no home outside the sidebar before.
        MenuEntry.Group(
            "&Organise",
            MenuEntry.Of(AppCommand.RenameSelection),
            MenuEntry.Of(AppCommand.DeleteSelection),
            MenuEntry.AfterRule(AppCommand.ToggleFavourite),
            MenuEntry.AfterRule(AppCommand.CommentOnProject)),

        MenuEntry.Group(
            "&Help",
            MenuEntry.Of(AppCommand.CheckForUpdates),
            MenuEntry.Of(AppCommand.About)),
    ];

    /// <summary>Every command an entry list can raise, including the ones nested under a heading.</summary>
    public static IEnumerable<AppCommand> Commands(IEnumerable<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Command != AppCommand.None)
                yield return entry.Command;

            foreach (var child in Commands(entry.Children ?? []))
                yield return child;
        }
    }
}
