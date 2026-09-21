using Termyn.Core.Sync;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The room a sidebar heading is given to be written in.
/// </summary>
/// <remarks>
/// A heading is set in bold and the tree measures every row with its own font, so the row it hands
/// over is a regular-weight row's width — and the last letter of a bold word is cut off the end of
/// it. Measured here rather than looked at: at a hundred per cent it's six pixels and an 's', and
/// on a scaled display it's a good deal more.
/// </remarks>
public class SidebarHeadingTests
{
    private static TreeView Sidebar(MainForm window)
        => Every(window).OfType<TreeView>().First();

    private static IEnumerable<Control> Every(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;

            foreach (var nested in Every(child))
                yield return nested;
        }
    }

    [WinFormsFact]
    public void The_row_the_tree_offers_a_heading_is_too_narrow_for_it()
    {
        // The reason the drawing is taken over at all. If this ever stops being true — a heading
        // set in the ordinary face, say — the widening below has nothing left to do.
        using var window = TestWindow.Build("sidebar-heading-room.json", new InMemorySnapshotStore());
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-2200, -2200);
        window.Show();

        var tree = Sidebar(window);
        var heading = tree.Nodes.Cast<TreeNode>().First(n => n.NodeFont is not null);

        using var bold = new Font(heading.NodeFont!, heading.NodeFont!.Style);
        var wanted = TextRenderer.MeasureText(heading.Text, bold).Width;

        Assert.True(
            wanted > heading.Bounds.Width,
            $"'{heading.Text}' wants {wanted}px and the tree offered {heading.Bounds.Width}px");
    }

    [WinFormsFact]
    public void Widening_a_row_gives_it_the_rest_of_the_sidebar()
    {
        using var window = TestWindow.Build("sidebar-heading-widened.json", new InMemorySnapshotStore());
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-2200, -2200);
        window.Show();

        var tree = Sidebar(window);
        var heading = tree.Nodes.Cast<TreeNode>().First(n => n.NodeFont is not null);

        var room = MainForm.Widened(heading.Bounds, tree.ClientSize.Width);
        var wanted = TextRenderer.MeasureText(heading.Text, heading.NodeFont!).Width;

        Assert.True(room.Width >= wanted, $"widened to {room.Width}px for text wanting {wanted}px");

        // And nothing else about the row moves: it starts where it started and is as tall as it was.
        Assert.Equal(heading.Bounds.Location, room.Location);
        Assert.Equal(heading.Bounds.Height, room.Height);
    }

    [WinFormsTheory]
    [InlineData(40, 220, 180)]   // a row the tree cut short gets the rest of the width
    [InlineData(40, 30, 60)]     // a sidebar dragged narrower than the row never shrinks it
    public void A_row_is_never_given_less_room_than_the_tree_offered(int x, int width, int expected)
    {
        var offered = new Rectangle(x, 0, 60, 18);

        Assert.Equal(expected, MainForm.Widened(offered, width).Width);
    }
}
