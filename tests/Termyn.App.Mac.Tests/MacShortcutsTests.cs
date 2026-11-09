using Termyn.Presentation;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// The Mac's keys for the task commands: that they don't collide, how they're written, and that a
/// key press finds its way back to the command it belongs to.
/// </summary>
public class MacShortcutsTests
{
    internal const ushort ReturnKey = 36;
    internal const ushort DeleteKey = 51;
    internal const ushort DownKey = 125;
    internal const ushort UpKey = 126;

    private static readonly List<AppCommand> Keyed = Enum.GetValues<AppCommand>().Where(c => MacShortcuts.For(c) is not null).ToList();

    [AppKitFact]
    public void No_two_commands_answer_to_the_same_key()
    {
        // Ignoring case because that's how a press is matched: ⇧⌘N arrives as a capital.
        Assert.NotEmpty(Keyed);

        var taken = Keyed
            .GroupBy(c => (MacShortcuts.For(c)!.Value.Key.ToUpperInvariant(), MacShortcuts.For(c)!.Value.Modifiers))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" and ", g));

        Assert.Empty(taken);
    }

    [AppKitFact]
    public void A_key_is_written_the_way_a_Mac_menu_writes_it()
    {
        Assert.Equal("⌘N", MacShortcuts.Text(AppCommand.NewTask));
        Assert.Equal("⇧⌘N", MacShortcuts.Text(AppCommand.NewSubtask));
        Assert.Equal("⇧⌘D", MacShortcuts.Text(AppCommand.Deadline));
        Assert.Equal("⇧⌘R", MacShortcuts.Text(AppCommand.Reminders));
        Assert.Equal("⌥⌘1", MacShortcuts.Text(AppCommand.Priority1));
        Assert.Equal("⌘]", MacShortcuts.Text(AppCommand.Indent));
        Assert.Equal("⌘↩", MacShortcuts.Text(AppCommand.ToggleComplete));
        Assert.Equal("⌘⌫", MacShortcuts.Text(AppCommand.Delete));
        Assert.Equal("⌥⌘↑", MacShortcuts.Text(AppCommand.MoveUp));
        Assert.Equal("⌥⌘↓", MacShortcuts.Text(AppCommand.MoveDown));
    }

    [AppKitFact]
    public void A_command_with_no_key_is_written_as_nothing()
    {
        // Return renames, but that's the list's key rather than the menu's.
        Assert.Null(MacShortcuts.For(AppCommand.Rename));
        Assert.Equal(string.Empty, MacShortcuts.Text(AppCommand.Rename));
    }

    [AppKitFact]
    public void Each_key_in_the_table_finds_its_command_again()
    {
        // Pressed as the table writes it. What the keyboard itself sends is the real-press test's.
        foreach (var command in Keyed)
        {
            var key = MacShortcuts.For(command)!.Value;

            Assert.Equal(command, MacShortcuts.CommandFor(Press(key.Key, key.Modifiers)));
        }
    }

    [AppKitFact]
    public void A_capital_finds_the_same_command_as_its_small_letter()
    {
        // What the Shift of ⇧⌘N actually delivers.
        Assert.Equal(AppCommand.NewSubtask, MacShortcuts.CommandFor(Press("N", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask)));
    }

    [AppKitFact]
    public void The_flags_an_arrow_comes_with_dont_stop_it_being_found()
    {
        // Every press of an arrow says it's a function key on the numeric pad as well.
        var press = Press(
            "\uF700",
            NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask
                | NSEventModifierMask.NumericPadKeyMask | NSEventModifierMask.FunctionKeyMask);

        Assert.Equal(AppCommand.MoveUp, MacShortcuts.CommandFor(press));
    }

    [AppKitFact]
    public void A_key_nothing_wants_finds_nothing()
    {
        Assert.Null(MacShortcuts.CommandFor(Press("q", NSEventModifierMask.CommandKeyMask)));

        // The right letter without the modifiers it asks for.
        Assert.Null(MacShortcuts.CommandFor(Press("n", 0)));
    }

    [AppKitFact]
    public void A_real_press_of_the_keys_that_arent_letters_finds_its_command()
    {
        // Built from the key itself rather than the table's idea of it, so AppKit says what it
        // reports. By key code, which for these doesn't move with the keyboard's layout. ⌘⌫ is the
        // one that differs: it reports \u007F where the table has the \b a menu item wants.
        Assert.Equal(AppCommand.Delete, MacShortcuts.CommandFor(RealPress(DeleteKey, CGEventFlags.Command)));
        Assert.Equal(AppCommand.ToggleComplete, MacShortcuts.CommandFor(RealPress(ReturnKey, CGEventFlags.Command)));
        Assert.Equal(AppCommand.MoveUp, MacShortcuts.CommandFor(RealPress(UpKey, CGEventFlags.Command | CGEventFlags.Alternate)));
        Assert.Equal(AppCommand.MoveDown, MacShortcuts.CommandFor(RealPress(DownKey, CGEventFlags.Command | CGEventFlags.Alternate)));
    }

    /// <summary>A press made from the key itself, with what AppKit makes of it filled in.</summary>
    /// <param name="keyCode">The key, by its virtual key code</param>
    /// <param name="modifiers">What's held with it</param>
    internal static NSEvent RealPress(ushort keyCode, CGEventFlags modifiers)
    {
        using var press = new CGEvent(null, keyCode, true) { Flags = modifiers };
        return NSEvent.Create(press)!;
    }

    /// <summary>A key press, of the sort AppKit hands a responder.</summary>
    private static NSEvent Press(string key, NSEventModifierMask modifiers)
        => NSEvent.KeyEvent(NSEventType.KeyDown, CGPoint.Empty, modifiers, 0, 0, null, key, key, false, 0)!;
}
