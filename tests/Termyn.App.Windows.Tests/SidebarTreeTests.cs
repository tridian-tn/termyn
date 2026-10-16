using System.Reflection;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Which drop the sidebar works out from where the pointer is during a drag.
/// </summary>
/// <remarks>
/// Asked of the tree directly rather than by dragging, for the reason the outline's are: a drag runs
/// a loop of its own with the real mouse. Whether a drop is allowed is the presenter's, tested in
/// <c>SidebarDraggingTests</c>.
/// </remarks>
public class SidebarTreeTests
{
    private static SidebarNode Project(string id) => new(SidebarKind.Project, id, id, 0, SidebarKeys.For(SidebarKind.Project, id));

    private static SidebarNode Section(string id) => new(SidebarKind.Section, id, id, 1, SidebarKeys.For(SidebarKind.Section, id));

    private static Form Window()
        => new() { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(300, 400) };

    /// <summary>"Work", open, with two sections under it, then "Home" with nothing under it.</summary>
    private static SidebarTree Tree(
        Form form,
        Func<SidebarNode, SidebarNode, bool, bool>? canDropRow = null,
        Func<string, SidebarNode, bool>? canDropTask = null)
    {
        var tree = new SidebarTree
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            ShowLines = false,
            CanDropRow = canDropRow ?? ((_, _, _) => true),
            CanDropTask = canDropTask ?? ((_, _) => true),
        };
        form.Controls.Add(tree);
        form.Show();

        var work = new TreeNode("Work") { Tag = Project("p") };
        work.Nodes.Add(new TreeNode("Next") { Tag = Section("s1") });
        work.Nodes.Add(new TreeNode("Later") { Tag = Section("s2") });
        tree.Nodes.Add(work);
        tree.Nodes.Add(new TreeNode("Home") { Tag = Project("q") });
        tree.ExpandAll();

        return tree;
    }

    private static TreeNode Work(SidebarTree tree) => tree.Nodes[0];

    private static TreeNode Later(SidebarTree tree) => tree.Nodes[0].Nodes[1];

    private static TreeNode Home(SidebarTree tree) => tree.Nodes[1];

    /// <summary>A point a given way down a row.</summary>
    private static Point In(TreeNode row, double down, int x = 40)
        => new(x, row.Bounds.Top + (int)(row.Bounds.Height * down));

    [WinFormsFact]
    public void The_top_half_of_a_row_is_before_it_and_the_bottom_half_after()
    {
        using var form = Window();
        using var tree = Tree(form);
        var home = Home(tree);

        Assert.Equal(new SidebarDrop(Project("q"), DropZone.Before, home, home.Bounds.Left), tree.RowDropAt(Project("x"), In(home, 0.25)));
        Assert.Equal(new SidebarDrop(Project("q"), DropZone.After, home, home.Bounds.Left), tree.RowDropAt(Project("x"), In(home, 0.75)));
    }

    [WinFormsFact]
    public void After_an_open_row_is_marked_under_the_last_row_shown_beneath_it()
    {
        // That's where a project put down after it lands: below its sections, not between it and them.
        using var form = Window();
        using var tree = Tree(form);
        var work = Work(tree);

        Assert.Equal(new SidebarDrop(Project("p"), DropZone.After, Later(tree), work.Bounds.Left), tree.RowDropAt(Project("x"), In(work, 0.75)));

        work.Collapse();
        Assert.Equal(new SidebarDrop(Project("p"), DropZone.After, work, work.Bounds.Left), tree.RowDropAt(Project("x"), In(work, 0.75)));
    }

    [WinFormsFact]
    public void A_task_goes_onto_the_row_wherever_on_it_the_pointer_is()
    {
        using var form = Window();
        using var tree = Tree(form);
        var later = Later(tree);
        var onto = new SidebarDrop(Section("s2"), DropZone.Onto, later, later.Bounds.Left);

        Assert.Equal(onto, tree.TaskDropAt("t", In(later, 0.2)));
        Assert.Equal(onto, tree.TaskDropAt("t", In(later, 0.8)));
        Assert.Equal(onto, tree.TaskDropAt("t", In(later, 0.5, x: tree.ClientSize.Width - 5)));
    }

    [WinFormsFact]
    public void Below_the_last_row_is_nowhere_to_drop()
    {
        using var form = Window();
        using var tree = Tree(form);

        Assert.Null(tree.TaskDropAt("t", new Point(40, Home(tree).Bounds.Bottom + 30)));
        Assert.Null(tree.RowDropAt(Project("x"), new Point(40, Home(tree).Bounds.Bottom + 30)));
    }

    [WinFormsFact]
    public void A_drop_the_presenter_turns_away_has_nowhere_to_land()
    {
        using var form = Window();
        using var tree = Tree(form, canDropRow: (_, _, _) => false, canDropTask: (_, _) => false);

        Assert.Null(tree.RowDropAt(Project("x"), In(Home(tree), 0.25)));
        Assert.Null(tree.TaskDropAt("t", In(Home(tree), 0.5)));
    }

    [WinFormsFact]
    public void The_presenter_is_asked_once_while_the_pointer_stays_over_the_same_drop()
    {
        var asked = 0;
        using var form = Window();
        using var tree = Tree(form, canDropRow: (_, _, _) => ++asked > 0);
        var home = Home(tree);

        tree.RowDropAt(Project("x"), In(home, 0.1));
        tree.RowDropAt(Project("x"), In(home, 0.2));
        tree.RowDropAt(Project("x"), In(home, 0.3));
        Assert.Equal(1, asked);

        // Into the other half, and then the same drop for something else being dragged.
        tree.RowDropAt(Project("x"), In(home, 0.8));
        tree.RowDropAt(Project("y"), In(home, 0.8));
        Assert.Equal(3, asked);
    }

    [WinFormsFact]
    public void A_drag_arriving_over_the_tree_is_asked_about_afresh()
    {
        // The tree isn't always rebuilt between two drags of the same task, and the first drop can
        // have changed the answer: a sub-task dropped on its own project comes out from under its
        // parent, and a second drop there would then do nothing.
        var asked = 0;
        using var form = Window();
        using var tree = Tree(form, canDropTask: (_, _) => ++asked > 0);
        tree.DraggedTask = () => "t";
        var over = In(Home(tree), 0.5);

        tree.TaskDropAt("t", over);
        Enter(tree, over);

        Assert.Equal(2, asked);
    }

    /// <summary>
    /// Has a drag arrive over the tree, the way the drag loop says so, without a real drag.
    /// </summary>
    private static void Enter(SidebarTree tree, Point client)
    {
        var screen = tree.PointToScreen(client);
        var arriving = new DragEventArgs(null, 0, screen.X, screen.Y, DragDropEffects.Move, DragDropEffects.None);

        typeof(Control).GetMethod("OnDragEnter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(tree, [arriving]);
    }
}
