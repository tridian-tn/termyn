using System.Text;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The Mac's keys for the task commands, which follow the Mac's conventions rather than translate
/// the Windows table key for key.
/// </summary>
/// <remarks>
/// ⌘ where Windows has Ctrl, but not only that: Return renames, as it does in Finder; ⌫ deletes;
/// and priorities are ⌥⌘1–4, because ⌘1–3 open the views, as Mail's mailboxes do. The keys that
/// only work in the task list — Space, Return, ⌫ — aren't here: they're the list's, and a menu
/// can't hold a key that also has to type into a text field.
/// </remarks>
internal static class MacShortcuts
{
    /// <summary>A menu item's key equivalent: the character it answers to and the modifiers with it.</summary>
    public readonly record struct Equivalent(string Key, NSEventModifierMask Modifiers);

    private const NSEventModifierMask Command = NSEventModifierMask.CommandKeyMask;
    private const NSEventModifierMask OptionCommand = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask;

    /// <summary>What a menu item's key equivalent is for the keys that aren't characters.</summary>
    private const string Return = "\r";
    private const string Backspace = "\b";

    private static readonly Dictionary<AppCommand, Equivalent> Table = new()
    {
        [AppCommand.NewTask] = new("n", Command),
        [AppCommand.ToggleComplete] = new(Return, Command),
        [AppCommand.Due] = new("d", Command),
        [AppCommand.Priority1] = new("1", OptionCommand),
        [AppCommand.Priority2] = new("2", OptionCommand),
        [AppCommand.Priority3] = new("3", OptionCommand),
        [AppCommand.Priority4] = new("4", OptionCommand),
        [AppCommand.ShowInTodoist] = new("o", Command),
        [AppCommand.Delete] = new(Backspace, Command),
        [AppCommand.Undo] = new("z", Command),
    };

    /// <summary>The key a command's menu item answers to, or null when it has none.</summary>
    public static Equivalent? For(AppCommand command) => Table.TryGetValue(command, out var found) ? found : null;

    /// <summary>
    /// A command's key written the way a Mac menu writes it — ⌥⌘1, ⌘⌫ — for text that names one.
    /// </summary>
    /// <param name="command">The command</param>
    /// <returns>The key, or empty when it has none</returns>
    public static string Text(AppCommand command)
    {
        if (For(command) is not { } key)
            return string.Empty;

        // The order every Mac menu puts them in.
        var text = new StringBuilder();
        if (key.Modifiers.HasFlag(NSEventModifierMask.ControlKeyMask))
            text.Append('⌃');
        if (key.Modifiers.HasFlag(NSEventModifierMask.AlternateKeyMask))
            text.Append('⌥');
        if (key.Modifiers.HasFlag(NSEventModifierMask.ShiftKeyMask))
            text.Append('⇧');
        if (key.Modifiers.HasFlag(NSEventModifierMask.CommandKeyMask))
            text.Append('⌘');

        text.Append(key.Key switch
        {
            Return => "↩",
            Backspace => "⌫",
            _ => key.Key.ToUpperInvariant(),
        });

        return text.ToString();
    }
}
