using Termyn.Core.Settings;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The outline's column headers, which are drawn in the theme rather than as the stock header.
/// </summary>
/// <remarks>
/// The stock background is light whatever the theme, which left a white strip across the top of
/// the list in a dark window. Drawn onto a bitmap, since the header is a native window a test can't
/// capture.
/// </remarks>
public class OutlineHeaderTests
{
    private const int Width = 160;
    private const int Height = 24;

    [WinFormsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_header_is_drawn_in_the_themes_colours(bool dark)
    {
        var theme = Theme.From(dark ? ThemePalette.Dark : ThemePalette.Light);
        using var outline = new OutlineView { Theme = theme };
        using var header = new ColumnHeader { Text = "Task" };
        using var bitmap = new Bitmap(Width, Height);

        using (var g = Graphics.FromImage(bitmap))
            outline.DrawHeader(g, new Rectangle(0, 0, Width, Height), header);

        // Clear of the text, which starts at the left.
        Assert.Equal(theme.Panel.ToArgb(), bitmap.GetPixel(Width - 20, 3).ToArgb());

        // The divider at the right, and the line along the bottom.
        Assert.Equal(theme.Border.ToArgb(), bitmap.GetPixel(Width - 1, Height / 2).ToArgb());
        Assert.Equal(theme.Border.ToArgb(), bitmap.GetPixel(Width / 2, Height - 1).ToArgb());
    }
}
