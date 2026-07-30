using System.Diagnostics.CodeAnalysis;

namespace Termyn.Core.Settings;

/// <summary>Modifier keys a global hotkey can be held with.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,

    /// <summary>The Windows / Super / Command key.</summary>
    Meta = 8,
}

/// <summary>
/// A system-wide key combination, held as a portable description rather than an OS key code. The
/// platform layer maps <see cref="Key"/> onto whatever its own registration API wants.
/// </summary>
/// <remarks>
/// At least one of Ctrl, Alt or Meta is required. A global hotkey takes the key away from every
/// other application, so a bare letter — or Shift and a letter — would swallow ordinary typing
/// everywhere on the machine.
/// </remarks>
public sealed record HotkeyBinding(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>The keys a hotkey may end on, upper-cased, in the order the settings UI offers them.</summary>
    public static readonly IReadOnlyList<string> AllowedKeys =
    [
        .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()),
        .. Enumerable.Range(0, 10).Select(d => d.ToString()),
        .. Enumerable.Range(1, 12).Select(n => "F" + n),
        "SPACE", "INSERT", "HOME", "END", "PAGEUP", "PAGEDOWN", "UP", "DOWN", "LEFT", "RIGHT",
    ];

    private static readonly HashSet<string> Allowed = new(AllowedKeys, StringComparer.OrdinalIgnoreCase);

    /// <summary>Quick-add from anywhere, per the spec's keyboard map.</summary>
    public static readonly HotkeyBinding Default = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "A");

    /// <summary>Whether this combination is one the platform can be asked to register.</summary>
    public bool IsValid
        => Allowed.Contains(Key) && (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Meta)) != 0;

    /// <summary>
    /// Reads a binding written as <c>Ctrl+Alt+A</c>. Returns false for anything unreadable or not
    /// registrable, so a hand-edited config falls back to the default rather than silently losing
    /// the hotkey.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var modifiers = HotkeyModifiers.None;
        string? key = null;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= HotkeyModifiers.Control; break;
                case "ALT": modifiers |= HotkeyModifiers.Alt; break;
                case "SHIFT": modifiers |= HotkeyModifiers.Shift; break;
                case "WIN" or "META" or "SUPER" or "CMD": modifiers |= HotkeyModifiers.Meta; break;
                default:
                    // A second non-modifier means this isn't one combination.
                    if (key is not null)
                        return false;
                    key = part.ToUpperInvariant();
                    break;
            }
        }

        if (key is null)
            return false;

        var parsed = new HotkeyBinding(modifiers, key);
        if (!parsed.IsValid)
            return false;

        binding = parsed;
        return true;
    }

    /// <summary>Reads a binding, falling back to <paramref name="fallback"/> when it can't be read.</summary>
    public static HotkeyBinding ParseOrDefault(string? text, HotkeyBinding? fallback = null)
        => TryParse(text, out var parsed) ? parsed : fallback ?? Default;

    /// <summary>Renders the binding the way <see cref="TryParse"/> reads it, and the UI shows it.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}
