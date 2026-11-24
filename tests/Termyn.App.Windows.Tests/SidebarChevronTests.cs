using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The arrow after a collapsed sidebar heading: which rows get it, and where it goes.
/// </summary>
public class SidebarChevronTests
{
    private static readonly Size Arrow = new(4, 8);

    private static TreeNode Heading(params string[] rows)
    {
        var heading = new TreeNode("Labels") { Tag = new SidebarNode(SidebarKind.Header, "Labels", "Labels", 0, "header:Labels") };

        foreach (var row in rows)
            heading.Nodes.Add(new TreeNode(row) { Tag = new SidebarNode(SidebarKind.Label, row, row, 1, "label:" + row) });

        return heading;
    }

    [WinFormsFact]
    public void A_collapsed_heading_with_rows_under_it_shows_the_arrow()
    {
        using var tree = new TreeView();
        var heading = Heading("errands", "calls");
        tree.Nodes.Add(heading);
        tree.CreateControl();

        heading.Collapse();

        Assert.True(MainForm.ShowsChevron(heading));
    }

    [WinFormsFact]
    public void An_open_heading_shows_no_arrow()
    {
        using var tree = new TreeView();
        var heading = Heading("errands");
        tree.Nodes.Add(heading);
        tree.CreateControl();

        heading.Expand();

        Assert.False(MainForm.ShowsChevron(heading));
    }

    [WinFormsFact]
    public void An_empty_heading_shows_no_arrow()
    {
        // Projects is listed even with no projects in it, and a closed one would claim there's
        // something to open.
        using var tree = new TreeView();
        var heading = Heading();
        tree.Nodes.Add(heading);
        tree.CreateControl();

        heading.Collapse();

        Assert.False(MainForm.ShowsChevron(heading));
    }

    [WinFormsFact]
    public void A_collapsed_project_shows_no_arrow_of_its_own()
    {
        // A project's rows have the tree's own expander, so a second arrow would be two marks for
        // one thing.
        using var tree = new TreeView();
        var project = new TreeNode("Work") { Tag = new SidebarNode(SidebarKind.Project, "1", "Work", 1, "project:1") };
        project.Nodes.Add(new TreeNode("Planning") { Tag = new SidebarNode(SidebarKind.Section, "2", "Planning", 2, "section:2") });
        tree.Nodes.Add(project);
        tree.CreateControl();

        project.Collapse();

        Assert.False(MainForm.ShowsChevron(project));
    }

    [WinFormsFact]
    public void The_arrow_follows_the_name_with_the_gap_between()
    {
        var (text, arrow) = SidebarChevron.Layout(new Rectangle(10, 20, 200, 18), textWidth: 50, gap: 5, Arrow);

        Assert.Equal(new Rectangle(10, 20, 50, 18), text);
        Assert.Equal(new Rectangle(65, 25, 4, 8), arrow);
    }

    [WinFormsFact]
    public void On_a_narrow_sidebar_the_name_gives_way_and_the_arrow_stays_inside()
    {
        var bounds = new Rectangle(10, 20, 40, 18);
        var (text, arrow) = SidebarChevron.Layout(bounds, textWidth: 50, gap: 5, Arrow);

        Assert.Equal(31, text.Width);
        Assert.Equal(bounds.Right, arrow.Right);
    }

    [WinFormsFact]
    public void A_row_too_narrow_for_the_gap_keeps_the_arrow_inside_it()
    {
        // Six pixels: room for the arrow but not the gap in front of it. The name gets nothing,
        // never less than nothing, and the gap gives way so the arrow isn't drawn past the edge.
        var bounds = new Rectangle(10, 20, 6, 18);
        var (text, arrow) = SidebarChevron.Layout(bounds, textWidth: 50, gap: 5, Arrow);

        Assert.Equal(0, text.Width);
        Assert.Equal(bounds.Right, arrow.Right);
    }

    [WinFormsFact]
    public void A_row_too_narrow_for_the_arrow_starts_it_at_the_row_rather_than_before_it()
    {
        var bounds = new Rectangle(10, 20, 2, 18);
        var (_, arrow) = SidebarChevron.Layout(bounds, textWidth: 50, gap: 5, Arrow);

        Assert.Equal(bounds.X, arrow.X);
    }
}
