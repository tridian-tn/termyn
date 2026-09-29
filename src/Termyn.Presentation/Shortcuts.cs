using Termyn.Core.Settings;
using static Termyn.Presentation.ShortcutScope;

namespace Termyn.Presentation;

/// <summary>Where a keystroke has to be pressed for it to mean what the table says.</summary>
public enum ShortcutScope
{
    /// <summary>Anywhere in the window, whatever has the focus.</summary>
    Window,

    /// <summary>With the task outline focused.</summary>
    Outline,

    /// <summary>With the sidebar focused.</summary>
    Sidebar,
}

/// <summary>One keystroke the app answers to, what it asks for, and where.</summary>
/// <param name="Keystroke">The keys to press</param>
/// <param name="Command">What pressing them asks for</param>
/// <param name="Scope">Where they have to be pressed</param>
public sealed record Shortcut(Keystroke Keystroke, AppCommand Command, ShortcutScope Scope);

/// <summary>
/// Every keystroke the app answers to, and what it asks for.
/// </summary>
/// <remarks>
/// One table, read from both directions: the key handlers match against it, and the menus print
/// from it — so a menu can't advertise a shortcut nothing is bound to. Where a command answers to
/// two keystrokes the first is the one written down.
///
/// Here rather than in the window because which key means what is the same answer on any desktop,
/// and so is the reasoning written beside each line. What stays with a toolkit is only how its own
/// key codes become a <see cref="Keystroke"/>.
/// </remarks>
public static class Shortcuts
{
    private const HotkeyModifiers None = HotkeyModifiers.None;
    private const HotkeyModifiers Ctrl = HotkeyModifiers.Control;
    private const HotkeyModifiers Shift = HotkeyModifiers.Shift;
    private const HotkeyModifiers Alt = HotkeyModifiers.Alt;

    /// <summary>The table, in the order a command's printed keystroke is chosen from.</summary>
    public static readonly IReadOnlyList<Shortcut> All =
    [
        // On a row of the outline.
        Bind(None, Key.Space, AppCommand.ToggleComplete, Outline),
        Bind(Ctrl, Key.Enter, AppCommand.ToggleComplete, Outline),
        Bind(None, Key.F2, AppCommand.Rename, Outline),
        Bind(Ctrl | Shift, Key.N, AppCommand.NewSubtask, Outline),
        Bind(Ctrl, Key.D, AppCommand.Due, Outline),

        // Shift on the same letter, because it's the same question about a different date.
        Bind(Ctrl | Shift, Key.D, AppCommand.Deadline, Outline),
        Bind(Ctrl, Key.Digit1, AppCommand.Priority1, Outline),
        Bind(Ctrl, Key.Digit2, AppCommand.Priority2, Outline),
        Bind(Ctrl, Key.Digit3, AppCommand.Priority3, Outline),
        Bind(Ctrl, Key.Digit4, AppCommand.Priority4, Outline),
        Bind(Ctrl, Key.L, AppCommand.Labels, Outline),
        Bind(Ctrl, Key.R, AppCommand.Reminders, Outline),
        // Ctrl and an arrow, all four of them: the two that change a task's depth and the two
        // that change its place, laid out the way the outline itself is. Tab is left to move
        // the focus, which is the one thing every other window in Windows uses it for.
        Bind(Ctrl, Key.Right, AppCommand.Indent, Outline),
        Bind(Ctrl, Key.Left, AppCommand.Outdent, Outline),
        Bind(Ctrl, Key.Up, AppCommand.MoveUp, Outline),
        Bind(Ctrl, Key.Down, AppCommand.MoveDown, Outline),

        // M for move. Only in the outline, where there's a task under the cursor to send somewhere:
        // window-wide it would fire over the description editor and the comment box as well.
        Bind(Ctrl, Key.M, AppCommand.MoveTo, Outline),

        // O for open, and the outline's alone for the same reason as the move: it's the task under
        // the cursor that goes to the browser.
        Bind(Ctrl, Key.O, AppCommand.ShowInTodoist, Outline),
        Bind(None, Key.Delete, AppCommand.Delete, Outline),

        // Kept off the window, where it would take Ctrl+Z away from every text box in it — undoing
        // a queued write instead of the word the user has just typed.
        Bind(Ctrl, Key.Z, AppCommand.Undo, Outline),

        // On a row of the sidebar. F2 and Delete belong to the outline as well, which is what the
        // scope is for: the same key acts on whichever list the user is actually in.
        Bind(None, Key.F2, AppCommand.RenameSelection, Sidebar),
        Bind(None, Key.Delete, AppCommand.DeleteSelection, Sidebar),

        // Modified: a bare letter is the tree's type-ahead, and favouriting is a write.
        Bind(Ctrl | Shift, Key.F, AppCommand.ToggleFavourite, Sidebar),

        // Shift as well as Ctrl, where a task makes do with Ctrl. Reordering the sidebar is a rare
        // and deliberate thing, and the harder reach is the point: a project shifted by a stray
        // finger is a change nobody sees happen and nobody thinks to look for.
        Bind(Ctrl | Shift, Key.Up, AppCommand.MoveSelectionUp, Sidebar),
        Bind(Ctrl | Shift, Key.Down, AppCommand.MoveSelectionDown, Sidebar),

        // Anywhere in the window.
        Bind(Ctrl, Key.N, AppCommand.NewTask, Window),
        Bind(None, Key.Insert, AppCommand.NewTask, Window),
        // Moved off Ctrl+Shift+N, which now adds a sub-task to whatever the outline is on. A new
        // project is the rarer of the two by a long way, and it keeps the shape of the shortcut.
        Bind(Ctrl | Alt | Shift, Key.N, AppCommand.NewProject, Window),
        Bind(None, Key.F5, AppCommand.SyncNow, Window),
        Bind(Ctrl, Key.H, AppCommand.ToggleCompleted, Window),
        // F4 shows and hides the panel; F6 and F7 pick which of its tabs is in front.
        Bind(None, Key.F4, AppCommand.ToggleDescription, Window),
        Bind(None, Key.F6, AppCommand.ViewDescription, Window),
        Bind(None, Key.F7, AppCommand.ViewComments, Window),

        // The key the menu shows first, then the number pad's own, which people reach for without
        // thinking and which is a different key entirely.
        Bind(Ctrl, Key.Plus, AppCommand.ZoomIn, Window),
        Bind(Ctrl, Key.NumPadPlus, AppCommand.ZoomIn, Window),
        Bind(Ctrl, Key.Minus, AppCommand.ZoomOut, Window),
        Bind(Ctrl, Key.NumPadMinus, AppCommand.ZoomOut, Window),
        Bind(Ctrl, Key.Digit0, AppCommand.ZoomReset, Window),
        Bind(Ctrl, Key.NumPad0, AppCommand.ZoomReset, Window),
        Bind(Ctrl, Key.F, AppCommand.Search, Window),
        Bind(Ctrl, Key.K, AppCommand.Palette, Window),
        // Alt and an arrow, since Ctrl and one now moves the task under the cursor. Moving a
        // task is the thing done often and from the outline; changing view is the rarer move
        // and has to work from anywhere, which is what earns it the window-wide binding.
        Bind(Alt, Key.Up, AppCommand.PreviousView, Window),
        Bind(Alt, Key.Down, AppCommand.NextView, Window),
        Bind(Ctrl, Key.Comma, AppCommand.Settings, Window),
    ];

    /// <summary>
    /// What a keystroke asks for where it was pressed.
    /// </summary>
    /// <param name="keystroke">What was pressed</param>
    /// <param name="scope">Where it was pressed</param>
    /// <returns>The command, or <see cref="AppCommand.None"/> when it asks for nothing there</returns>
    public static AppCommand CommandFor(Keystroke keystroke, ShortcutScope scope)
        => All.FirstOrDefault(s => s.Keystroke == keystroke && s.Scope == scope)?.Command ?? AppCommand.None;

    /// <summary>
    /// How a command's shortcut is written in a menu.
    /// </summary>
    /// <remarks>
    /// A menu that prints a shortcut nothing is bound to is the failure worth catching, which is
    /// why this reads the same table the key handlers do.
    /// </remarks>
    /// <param name="command">The command a menu entry runs</param>
    /// <returns>Its first keystroke as a menu writes it, or empty when it has none</returns>
    public static string ShortcutFor(AppCommand command)
        => All.FirstOrDefault(s => s.Command == command)?.Keystroke.ToString() ?? string.Empty;

    private static Shortcut Bind(HotkeyModifiers modifiers, Key key, AppCommand command, ShortcutScope scope)
        => new(new Keystroke(modifiers, key), command, scope);
}
