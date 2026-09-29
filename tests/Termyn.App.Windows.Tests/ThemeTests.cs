using Termyn.Core.Settings;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The palette as the window draws it.
/// </summary>
/// <remarks>
/// What each colour is, and how the derived ones are worked out, is the palette's and tested with
/// it in Core. What's left here is the window's half: that it asks the palette's rules about its own
/// colours, and so can't hold an answer for colours it no longer has.
/// </remarks>
public class ThemeTests
{
    [WinFormsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_derived_colours_are_the_palettes(bool dark)
    {
        var palette = dark ? ThemePalette.Dark : ThemePalette.Light;

        var theme = Theme.From(palette);

        Assert.Equal(Theme.ToColor(palette.OnAccent), theme.OnAccent);
        Assert.Equal(Theme.ToColor(palette.Unfocused), theme.Unfocused);
    }

    [WinFormsFact]
    public void A_copy_with_another_accent_fades_that_accent_for_an_unfocused_selection()
    {
        var light = Theme.From(ThemePalette.Light);
        var teal = Rgb.Parse("#158FAD");

        var changed = light with { Accent = Theme.ToColor(teal) };

        Assert.Equal(Theme.ToColor(ThemePalette.UnfocusedFor(teal, ThemePalette.Light.Background)), changed.Unfocused);
    }

    [WinFormsFact]
    public void A_copy_made_dark_writes_on_the_accent_in_its_background()
        => Assert.Equal(Theme.ToColor(ThemePalette.Light.Background), (Theme.From(ThemePalette.Light) with { IsDark = true }).OnAccent);
}
