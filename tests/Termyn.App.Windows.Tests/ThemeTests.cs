using Termyn.Core.Settings;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The palette as the window draws it.
/// </summary>
/// <remarks>
/// What each colour is, and how the derived ones are worked out, is the palette's and tested with
/// it in Core. What's left here is the handing over: eleven colours passed by position, where two
/// swapped would still compile.
/// </remarks>
public class ThemeTests
{
    [WinFormsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_derived_colours_come_from_the_palette(bool dark)
    {
        var palette = dark ? ThemePalette.Dark : ThemePalette.Light;

        var theme = Theme.From(palette);

        Assert.Equal(Theme.ToColor(palette.OnAccent), theme.OnAccent);
        Assert.Equal(Theme.ToColor(palette.Unfocused), theme.Unfocused);
    }
}
