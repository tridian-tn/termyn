using Termyn.Core.Model;

namespace Termyn.Core.Settings;

/// <summary>Which theme to use, or to follow whatever the desktop is set to.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>A colour, kept as plain bytes so Core stays free of any UI toolkit's colour type.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Reads <c>#RRGGBB</c>. Throws on anything else — these are compile-time constants.</summary>
    public static Rgb Parse(string hex)
    {
        var s = hex.AsSpan().TrimStart('#');
        if (s.Length != 6)
            throw new FormatException($"Expected a #RRGGBB colour, got \"{hex}\".");
        return new Rgb(
            byte.Parse(s[..2], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(s[2..4], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(s[4..], System.Globalization.NumberStyles.HexNumber));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>The colours one theme is drawn with — the amber-on-slate identity, light and dark.</summary>
public sealed record ThemePalette(
    bool IsDark,
    Rgb Accent,
    Rgb AccentHover,
    Rgb Background,
    Rgb Panel,
    Rgb Row,
    Rgb Border,
    Rgb TextPrimary,
    Rgb TextSecondary)
{
    public static readonly ThemePalette Dark = new(
        IsDark: true,
        Accent: Rgb.Parse("#F2A03C"),
        AccentHover: Rgb.Parse("#FFB25A"),
        Background: Rgb.Parse("#16181D"),
        Panel: Rgb.Parse("#1E2128"),
        Row: Rgb.Parse("#262A33"),
        Border: Rgb.Parse("#333844"),
        TextPrimary: Rgb.Parse("#E8EAED"),
        TextSecondary: Rgb.Parse("#9AA0AB"));

    public static readonly ThemePalette Light = new(
        IsDark: false,
        Accent: Rgb.Parse("#C77D1E"),
        AccentHover: Rgb.Parse("#A9660F"),
        Background: Rgb.Parse("#FBFBFA"),
        Panel: Rgb.Parse("#FFFFFF"),
        Row: Rgb.Parse("#F1F1EF"),
        Border: Rgb.Parse("#E2E2DF"),
        TextPrimary: Rgb.Parse("#1F2126"),
        TextSecondary: Rgb.Parse("#6B7079"));

    /// <summary>The text drawn on an accent-coloured background — the selected row.</summary>
    public Rgb OnAccent => IsDark ? Background : new Rgb(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// The selected row of a control that hasn't got the focus.
    /// </summary>
    /// <remarks>
    /// The accent, mostly faded into the background: the same colour the focused selection is, so it
    /// reads as the same thing rather than as a second kind of highlight, and quiet enough not to
    /// compete with the one that has the focus. Border was tried first and is about twenty units off
    /// the background in the light theme, which is to say invisible.
    /// </remarks>
    public Rgb Unfocused => Blend(Accent, Background, 0.78);

    /// <summary>Mixes two colours.</summary>
    /// <param name="from">The colour at 0</param>
    /// <param name="to">The colour at 1</param>
    /// <param name="amount">How far to travel, 0 to 1</param>
    /// <returns>The colour that far between them</returns>
    public static Rgb Blend(Rgb from, Rgb to, double amount) => new(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    /// <summary>
    /// Priority colours, which match Todoist's so a task reads the same here as in the web app.
    /// Shared by both themes, so a screenshot of one is recognisable next to the other.
    /// </summary>
    public static Rgb ForPriority(Priority priority) => priority switch
    {
        Priority.P1 => Rgb.Parse("#E4483A"),
        Priority.P2 => Rgb.Parse("#F5A623"),
        Priority.P3 => Rgb.Parse("#3B82F6"),
        _ => Rgb.Parse("#9AA0AB"),
    };

    /// <summary>
    /// The palette to draw with. <paramref name="systemPrefersLight"/> is the desktop's own setting,
    /// which only decides anything when the user hasn't chosen a theme themselves.
    /// </summary>
    public static ThemePalette For(ThemePreference preference, bool systemPrefersLight) => preference switch
    {
        ThemePreference.Light => Light,
        ThemePreference.Dark => Dark,
        _ => systemPrefersLight ? Light : Dark,
    };
}
