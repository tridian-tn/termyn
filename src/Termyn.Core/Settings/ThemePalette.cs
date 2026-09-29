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
    public Rgb OnAccent => OnAccentFor(IsDark, Background);

    /// <summary>The selected row of a control that hasn't got the focus.</summary>
    public Rgb Unfocused => UnfocusedFor(Accent, Background);

    /// <summary>
    /// The text drawn on an accent-coloured background, for a theme with this background.
    /// </summary>
    /// <remarks>
    /// Static so something holding the palette's colours in its own toolkit's type can ask about
    /// those, rather than keeping a copy of the answer that could fall behind them.
    /// </remarks>
    /// <param name="isDark">Whether the theme is dark</param>
    /// <param name="background">The theme's background</param>
    /// <returns>The background in a dark theme, white in a light one</returns>
    public static Rgb OnAccentFor(bool isDark, Rgb background) => isDark ? background : new Rgb(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// The selected row of a control that hasn't got the focus, for a theme with these colours.
    /// </summary>
    /// <remarks>
    /// The accent, mostly faded into the background: the same colour the focused selection is, so it
    /// reads as the same thing rather than as a second kind of highlight, and quiet enough not to
    /// compete with the one that has the focus. Border was tried first and is about twenty units off
    /// the background in the light theme, which is to say invisible.
    /// </remarks>
    /// <param name="accent">The theme's accent</param>
    /// <param name="background">The theme's background</param>
    /// <returns>The accent faded most of the way into the background</returns>
    public static Rgb UnfocusedFor(Rgb accent, Rgb background) => Blend(accent, background, 0.78);

    /// <summary>Mixes two colours.</summary>
    /// <param name="from">The colour at 0</param>
    /// <param name="to">The colour at 1</param>
    /// <param name="amount">How far to travel, 0 to 1</param>
    /// <returns>The colour that far between them</returns>
    /// <exception cref="ArgumentOutOfRangeException">The amount is outside 0 to 1, or not a number</exception>
    public static Rgb Blend(Rgb from, Rgb to, double amount)
    {
        // Past either end the channels would wrap round a byte rather than stop, and come back as
        // some other colour entirely. Asked this way round so a NaN is refused as well.
        if (!(amount >= 0 && amount <= 1))
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A blend runs from 0 to 1.");

        return new(
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

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
