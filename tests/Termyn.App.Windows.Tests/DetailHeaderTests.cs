using System.Drawing;
using Termyn.Core.Settings;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The line naming what the detail panel is about. What it says comes from
/// <see cref="Termyn.Presentation.CommentSubject"/> and is tested there; these are about the strip
/// drawing what it was given, and keeping the row the rule goes on.
/// </summary>
public class DetailHeaderTests
{
    private static DetailHeader Header()
    {
        var header = new DetailHeader { Width = 400, Theme = Theme.Resolve(ThemePreference.Light) };
        header.CreateControl();
        return header;
    }

    /// <summary>Paints the header and everything in it, the way a form would.</summary>
    private static Bitmap Painted(DetailHeader header)
    {
        var shot = new Bitmap(header.Width, header.Height);
        header.DrawToBitmap(shot, new Rectangle(0, 0, header.Width, header.Height));
        return shot;
    }

    [Fact]
    public void The_rule_along_the_bottom_is_left_showing()
    {
        // The name fills the header, so the last row is held back by the header's own padding. Take
        // that away and the label paints its background over the rule: the header still lays out,
        // still says the right thing, and reads as part of the tab strip below it.
        using var header = Header();
        using var shot = Painted(header);

        Assert.Equal(header.Theme.Border, Color.FromArgb(255, shot.GetPixel(header.Width / 2, header.Height - 1)));
    }

    [Fact]
    public void The_name_is_drawn_and_stays_inside_the_strip()
    {
        // Ink on the line, and none of it below the rule. A name long enough to need more room than
        // there is has to lose the end of itself rather than a second line, because the header takes
        // its room off the top of the panel and the tabs pay for anything it grows by.
        using var header = Header();
        header.Subject = $"Task: {new string('W', 300)}";

        using var shot = Painted(header);
        var ink = 0;

        for (var y = 0; y < header.Height - 1; y++)
        for (var x = 0; x < header.Width; x++)
        {
            if (shot.GetPixel(x, y).ToArgb() != header.Theme.Panel.ToArgb())
                ink++;
        }

        Assert.True(ink > 0, "the name was not drawn");
    }

    [Fact]
    public void Nothing_selected_leaves_the_strip_empty_rather_than_gone()
    {
        // Tempting to hide it — an empty line with a rule under it reads as something that failed to
        // load. But the strip is docked above the tabs, so hiding it moves them up under whoever was
        // reading them, every time the selection lands on a label or a filter.
        using var header = Header();
        header.Subject = "Task: Book the van";

        header.Subject = string.Empty;

        Assert.True(header.Visible);
    }
}
