using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// WinForms' key codes as the portable keystrokes the shortcut table is written in.
/// </summary>
/// <remarks>
/// Which keystroke means which command is Presentation's, and tested there in <c>ShortcutTests</c>.
/// These are the half that can't leave this project: that each code is the key it says it is, and
/// that everything the table binds can actually be pressed.
/// </remarks>
public class KeyMapTests
{
    [WinFormsFact]
    public void The_letters_digits_and_function_keys_each_land_on_the_key_of_the_same_name()
    {
        // The map counts through each run rather than naming every key, which only works while the
        // runs line up on both sides.
        for (var code = Keys.A; code <= Keys.Z; code++)
            Assert.Equal(code.ToString(), KeyMap.KeystrokeOf(code)?.Key.ToString());

        for (var digit = 0; digit <= 9; digit++)
            Assert.Equal($"Digit{digit}", KeyMap.KeystrokeOf(Keys.D0 + digit)?.Key.ToString());

        for (var code = Keys.F1; code <= Keys.F12; code++)
            Assert.Equal(code.ToString(), KeyMap.KeystrokeOf(code)?.Key.ToString());
    }

    [WinFormsTheory]
    [InlineData(Keys.Up, Key.Up)]
    [InlineData(Keys.Down, Key.Down)]
    [InlineData(Keys.Left, Key.Left)]
    [InlineData(Keys.Right, Key.Right)]
    [InlineData(Keys.Space, Key.Space)]
    [InlineData(Keys.Enter, Key.Enter)]
    [InlineData(Keys.Tab, Key.Tab)]
    [InlineData(Keys.Escape, Key.Escape)]
    [InlineData(Keys.Delete, Key.Delete)]
    [InlineData(Keys.Insert, Key.Insert)]
    [InlineData(Keys.Oemplus, Key.Plus)]
    [InlineData(Keys.OemMinus, Key.Minus)]
    [InlineData(Keys.Oemcomma, Key.Comma)]
    [InlineData(Keys.NumPad0, Key.NumPad0)]
    [InlineData(Keys.Add, Key.NumPadPlus)]
    [InlineData(Keys.Subtract, Key.NumPadMinus)]
    public void Each_named_key_is_the_key_it_says(Keys code, Key expected)
        => Assert.Equal(new Keystroke(HotkeyModifiers.None, expected), KeyMap.KeystrokeOf(code));

    [WinFormsFact]
    public void The_modifiers_come_across_with_the_key()
    {
        Assert.Equal(
            new Keystroke(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt, Key.N),
            KeyMap.KeystrokeOf(Keys.Control | Keys.Shift | Keys.Alt | Keys.N));
    }

    [WinFormsTheory]
    [InlineData(Keys.PrintScreen)]
    [InlineData(Keys.NumPad1)]
    [InlineData(Keys.Control | Keys.NumPad1)]
    public void A_key_no_shortcut_is_written_on_is_no_keystroke(Keys code)
    {
        // The pad's digits other than 0 among them: the row above the letters and the pad are
        // different keys, and Ctrl+1 on the pad doesn't set a priority the way Ctrl+1 above does.
        Assert.Null(KeyMap.KeystrokeOf(code));
        Assert.Equal(AppCommand.None, MainForm.CommandFor(code, ShortcutScope.Outline));
    }

    [WinFormsFact]
    public void Everything_the_table_binds_can_be_pressed()
    {
        // A keystroke nothing maps onto would sit in the table, print in a menu, and never fire.
        var codes = Enum.GetValues<Keys>().Where(k => k is > Keys.None and <= Keys.OemClear).ToList();

        foreach (var shortcut in Shortcuts.All)
        {
            var held = Keys.None;
            if (shortcut.Keystroke.Modifiers.HasFlag(HotkeyModifiers.Control))
                held |= Keys.Control;
            if (shortcut.Keystroke.Modifiers.HasFlag(HotkeyModifiers.Shift))
                held |= Keys.Shift;
            if (shortcut.Keystroke.Modifiers.HasFlag(HotkeyModifiers.Alt))
                held |= Keys.Alt;

            Assert.True(
                codes.Any(code => KeyMap.KeystrokeOf(held | code) == shortcut.Keystroke),
                $"Nothing on a Windows keyboard presses {shortcut.Keystroke} for {shortcut.Command}");
        }
    }

    [WinFormsFact]
    public void A_key_event_asks_the_table_what_it_means_where_it_happened()
    {
        // The window's own entry point, which every key handler goes through. The digit is the
        // case worth checking: WinForms calls it D1.
        Assert.Equal(AppCommand.Priority1, MainForm.CommandFor(Keys.Control | Keys.D1, ShortcutScope.Outline));
        Assert.Equal(AppCommand.RenameSelection, MainForm.CommandFor(Keys.F2, ShortcutScope.Sidebar));
        Assert.Equal(AppCommand.Settings, MainForm.CommandFor(Keys.Control | Keys.Oemcomma, ShortcutScope.Window));
    }
}
