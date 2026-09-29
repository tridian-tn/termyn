using Termyn.Core.Settings;

namespace Termyn.Presentation;

/// <summary>
/// A key a shortcut can end on, named for what's on the key rather than for how any toolkit
/// numbers it.
/// </summary>
/// <remarks>
/// Letters, digits and function keys each run in order with nothing between, so something mapping
/// its own key codes onto these can count through a run rather than name every member. The number
/// pad's keys are keys of their own: a toolkit reports them apart from the row above the letters,
/// and a shortcut on one isn't a shortcut on the other unless the table says so.
/// </remarks>
public enum Key
{
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    Digit0, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9,

    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    Up, Down, Left, Right,
    Space, Enter, Tab, Escape, Delete, Insert,

    /// <summary>The key beside Backspace, whatever its other legend says.</summary>
    Plus,

    /// <summary>The key beside the zero on the row of digits.</summary>
    Minus,
    Comma,

    NumPad0,
    NumPadPlus,
    NumPadMinus,
}

/// <summary>
/// A key and the modifiers held with it — one keystroke, as a shortcut names it.
/// </summary>
/// <param name="Modifiers">What's held down with the key</param>
/// <param name="Key">The key itself</param>
public readonly record struct Keystroke(HotkeyModifiers Modifiers, Key Key)
{
    /// <summary>
    /// The keystroke as a menu writes it — "Ctrl+1", "Ctrl+Shift+↑", "Del".
    /// </summary>
    /// <remarks>
    /// Modifiers in the order Windows menus put them. The digits are written as digits, which is
    /// the point of naming the keys here rather than borrowing a toolkit's names: WinForms' own
    /// converter calls Ctrl+1 "Ctrl+D1".
    /// </remarks>
    public override string ToString()
    {
        var parts = new List<string>(5);

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta))
            parts.Add("Win");

        parts.Add(Key switch
        {
            >= Key.Digit0 and <= Key.Digit9 => ((char)('0' + (Key - Key.Digit0))).ToString(),
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Left => "←",
            Key.Right => "→",
            Key.Delete => "Del",
            Key.Comma => ",",

            // Written the way a menu writes a zoom, whichever of the two keys it's on.
            Key.Plus or Key.NumPadPlus => "+",
            Key.Minus or Key.NumPadMinus => "-",
            Key.NumPad0 => "0",
            _ => Key.ToString(),
        });

        return string.Join("+", parts);
    }
}
