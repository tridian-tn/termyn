namespace Termyn.App.Windows.Tests;

/// <summary>
/// The room a sidebar heading is given to be written in.
/// </summary>
/// <remarks>
/// A heading is set in bold and a tree measures every row with its own font, so the row it hands
/// over is a regular-weight row's width — and the last letter of a bold word is cut off the end of
/// it. Measured rather than looked at: at a hundred per cent it's six pixels and an 's', and on a
/// scaled display a good deal more.
///
/// The tree here is built rather than taken off a window. What's in doubt is the control's own
/// measuring, which is the same wherever the tree is, and standing a whole main window up to read
/// it back costs the suite more than the tie to the sidebar is worth.
/// </remarks>
public class SidebarHeadingTests
{
    /// <summary>A tree set up the way the sidebar is, with one heading in bold.</summary>
    private static TreeView Sidebar(out TreeNode heading, out Font bold)
    {
        var tree = new TreeView
        {
            Width = 220,
            HideSelection = true,
            ShowLines = false,
            ShowRootLines = false,
            FullRowSelect = true,
            Indent = 14,
            BorderStyle = BorderStyle.None,
            DrawMode = TreeViewDrawMode.OwnerDrawText,
        };

        bold = new Font(tree.Font, FontStyle.Bold);
        heading = new TreeNode("Favourites") { NodeFont = bold };

        tree.Nodes.Add(heading);
        tree.CreateControl();

        return tree;
    }

    [WinFormsFact]
    public void The_row_the_tree_offers_a_heading_is_too_narrow_for_it()
    {
        // The reason the drawing is taken over at all. If a tree ever starts measuring a row by
        // the font the row is set in, the widening below has nothing left to do.
        using var tree = Sidebar(out var heading, out var bold);
        using var face = bold;

        var wanted = TextRenderer.MeasureText(heading.Text, bold).Width;

        Assert.True(
            wanted > heading.Bounds.Width,
            $"'{heading.Text}' wants {wanted}px and the tree offered {heading.Bounds.Width}px");
    }

    [WinFormsFact]
    public void Widening_a_row_gives_it_the_rest_of_the_sidebar()
    {
        using var tree = Sidebar(out var heading, out var bold);
        using var face = bold;

        var room = MainForm.Widened(heading.Bounds, tree.ClientSize.Width);

        Assert.True(
            room.Width >= TextRenderer.MeasureText(heading.Text, bold).Width,
            $"widened to {room.Width}px for text wanting {TextRenderer.MeasureText(heading.Text, bold).Width}px");

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
