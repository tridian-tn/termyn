using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// WinForms' key codes as the portable keystrokes the shortcut table is written in.
/// </summary>
/// <remarks>
/// The one part of the shortcuts that can't leave this project. Which keystroke means which command
/// is Presentation's (see <see cref="Shortcuts"/>); this only says which of WinForms' codes is
/// which key.
/// </remarks>
internal static class KeyMap
{
    /// <summary>
    /// The keystroke a WinForms key event describes.
    /// </summary>
    /// <remarks>
    /// The row of digits and the number pad come through as different codes, and stay different
    /// keys here, so a shortcut bound to one isn't quietly bound to the other as well.
    /// </remarks>
    /// <param name="keyData">The key and its modifiers, as a key event carries them</param>
    /// <returns>The keystroke, or null for a key no shortcut could be written on</returns>
    internal static Keystroke? KeystrokeOf(Keys keyData)
    {
        var modifiers = HotkeyModifiers.None;
        if (keyData.HasFlag(Keys.Control))
            modifiers |= HotkeyModifiers.Control;
        if (keyData.HasFlag(Keys.Shift))
            modifiers |= HotkeyModifiers.Shift;
        if (keyData.HasFlag(Keys.Alt))
            modifiers |= HotkeyModifiers.Alt;

        var code = keyData & Keys.KeyCode;
        Key? key = code switch
        {
            >= Keys.A and <= Keys.Z => Key.A + (code - Keys.A),
            >= Keys.D0 and <= Keys.D9 => Key.Digit0 + (code - Keys.D0),
            >= Keys.F1 and <= Keys.F12 => Key.F1 + (code - Keys.F1),
            Keys.Up => Key.Up,
            Keys.Down => Key.Down,
            Keys.Left => Key.Left,
            Keys.Right => Key.Right,
            Keys.Space => Key.Space,

            // Return and Enter are one code under two names.
            Keys.Return => Key.Enter,
            Keys.Tab => Key.Tab,
            Keys.Escape => Key.Escape,
            Keys.Delete => Key.Delete,
            Keys.Insert => Key.Insert,

            // Named after the keys' other legends, which aren't what they're for here.
            Keys.Oemplus => Key.Plus,
            Keys.OemMinus => Key.Minus,
            Keys.Oemcomma => Key.Comma,

            Keys.NumPad0 => Key.NumPad0,
            Keys.Add => Key.NumPadPlus,
            Keys.Subtract => Key.NumPadMinus,
            _ => null,
        };

        return key is { } pressed ? new Keystroke(modifiers, pressed) : null;
    }
}
