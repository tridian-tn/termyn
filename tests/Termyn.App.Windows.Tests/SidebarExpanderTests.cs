using System.Runtime.InteropServices;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The arrow at the head of a sidebar row that folds: where it is, and what a click on it does.
/// </summary>
/// <remarks>
/// The presses are sent to the tree's window as the messages a click would bring, rather than
/// moving the real pointer, which would fight with whoever's at the machine.
/// </remarks>
public class SidebarExpanderTests
{
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static Form Window()
        => new() { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(300, 400) };

    /// <summary>A "Labels" heading, open, with two labels under it, then "Inbox" with nothing under it.</summary>
    private static SidebarTree Tree(Form form)
    {
        var tree = new SidebarTree
        {
            Dock = DockStyle.Fill,
            ShowLines = false,
            ShowRootLines = false,
            ShowPlusMinus = false,
            FullRowSelect = true,
            Indent = 14,
        };
        form.Controls.Add(tree);
        form.Show();

        var labels = new TreeNode("Labels") { Tag = new SidebarNode(SidebarKind.Header, "Labels", "Labels", 0, "header:Labels") };
        labels.Nodes.Add(new TreeNode("errands") { Tag = new SidebarNode(SidebarKind.Label, "errands", "errands", 1, "label:errands") });
        labels.Nodes.Add(new TreeNode("calls") { Tag = new SidebarNode(SidebarKind.Label, "calls", "calls", 1, "label:calls") });
        tree.Nodes.Add(new TreeNode("Inbox") { Tag = new SidebarNode(SidebarKind.SmartView, "Inbox", "Inbox", 0, "Inbox") });
        tree.Nodes.Add(labels);
        tree.ExpandAll();
        tree.SelectedNode = tree.Nodes[0];

        return tree;
    }

    private static TreeNode Labels(SidebarTree tree) => tree.Nodes[1];

    /// <summary>The middle of the arrow's room on a row.</summary>
    private static Point OnArrow(SidebarTree tree, TreeNode row)
        => new(row.Bounds.Left + (tree.ExpanderRoom / 2), row.Bounds.Top + (row.Bounds.Height / 2));

    /// <summary>A point on a row's name, well past the arrow.</summary>
    private static Point OnName(SidebarTree tree, TreeNode row)
        => new(row.Bounds.Left + tree.ExpanderRoom + 10, row.Bounds.Top + (row.Bounds.Height / 2));

    /// <summary>
    /// Presses the left button at a point, with the release already waiting in the queue.
    /// </summary>
    /// <remarks>
    /// The release goes first so a press the tree handles itself finds it while it's watching for
    /// a drag, and stops watching. Sent after the press instead, it would only arrive once the tree
    /// had given up waiting for the real mouse, which with no real button down is whenever someone
    /// next moves it.
    /// </remarks>
    private static void Press(Control tree, int message, Point at)
    {
        var lParam = (IntPtr)((at.Y << 16) | (at.X & 0xFFFF));
        PostMessage(tree.Handle, WmLButtonUp, IntPtr.Zero, lParam);
        SendMessage(tree.Handle, message, (IntPtr)1, lParam);
        Application.DoEvents();
    }

    [WinFormsFact]
    public void A_click_on_the_arrow_folds_the_row_and_leaves_the_selection_where_it_was()
    {
        using var form = Window();
        var tree = Tree(form);

        Press(tree, WmLButtonDown, OnArrow(tree, Labels(tree)));

        Assert.False(Labels(tree).IsExpanded);
        Assert.Same(tree.Nodes[0], tree.SelectedNode);
    }

    [WinFormsFact]
    public void A_click_on_the_arrow_brings_the_focus_to_the_sidebar()
    {
        // From wherever it was, as a click anywhere else on the tree would: left behind, the arrow
        // keys go on moving through the outline after a row here has been folded.
        using var form = Window();
        var elsewhere = new TextBox { Dock = DockStyle.Top };
        form.Controls.Add(elsewhere);
        var tree = Tree(form);
        elsewhere.Focus();
        Assert.True(elsewhere.Focused);

        Press(tree, WmLButtonDown, OnArrow(tree, Labels(tree)));

        Assert.True(tree.Focused);
    }

    [WinFormsFact]
    public void A_second_quick_click_on_the_arrow_unfolds_it_again()
    {
        // The second press of two in quick succession arrives as a double-click, which the tree
        // handles itself. It has to toggle the row back without selecting it.
        using var form = Window();
        var tree = Tree(form);
        var arrow = OnArrow(tree, Labels(tree));

        Press(tree, WmLButtonDown, arrow);
        Press(tree, WmLButtonDblClk, arrow);

        Assert.True(Labels(tree).IsExpanded);
        Assert.Same(tree.Nodes[0], tree.SelectedNode);
    }

    [WinFormsFact]
    public void A_click_on_the_name_selects_the_row_and_folds_nothing()
    {
        using var form = Window();
        var tree = Tree(form);

        Press(tree, WmLButtonDown, OnName(tree, Labels(tree)));

        Assert.True(Labels(tree).IsExpanded);
        Assert.Same(Labels(tree), tree.SelectedNode);
    }

    [WinFormsFact]
    public void A_row_with_nothing_under_it_has_no_arrow_to_click()
    {
        using var form = Window();
        var tree = Tree(form);
        var inbox = tree.Nodes[0];

        Assert.Null(tree.ExpanderAt(OnArrow(tree, inbox)));
    }

    [WinFormsTheory]
    [InlineData(0, true)]     // the room's first pixel
    [InlineData(13, true)]    // its last
    [InlineData(14, false)]   // where the name starts
    [InlineData(-1, false)]   // the indent before it
    public void The_arrow_is_the_room_at_the_head_of_the_row(int across, bool on)
    {
        var row = new Rectangle(28, 40, 120, 18);

        Assert.Equal(on, SidebarTree.OnExpander(row, 14, new Point(row.Left + across, row.Top + 9)));
    }

    [WinFormsFact]
    public void A_press_left_of_the_window_is_read_as_a_negative_point()
    {
        // The halves are signed: a press captured while the pointer is past the tree's left edge
        // comes with a negative x, which read unsigned is a point far off to the right.
        var lParam = (IntPtr)((20 << 16) | (-3 & 0xFFFF));

        Assert.Equal(new Point(-3, 20), SidebarTree.PointOf(lParam));
    }

    [WinFormsFact]
    public void A_point_beside_the_arrow_but_on_another_row_is_not_on_it()
    {
        var row = new Rectangle(28, 40, 120, 18);

        Assert.False(SidebarTree.OnExpander(row, 14, new Point(row.Left + 5, row.Bottom)));
    }
}
