using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// What the window tells a menu about the row the sidebar is on.
/// </summary>
/// <remarks>
/// The menu tests build a context by hand and so never touch the window that fills one in. This is
/// the step between: that the answers the presenter works out actually reach the entries, which is
/// a wire nothing else pulls on.
/// </remarks>
public class SidebarMoveTests
{
    [WinFormsFact]
    public void The_window_tells_the_menu_where_a_project_could_go()
    {
        using var window = Window();

        var first = window.NewContext(Node("a"));
        var last = window.NewContext(Node("c"));
        var middle = window.NewContext(Node("b"));

        Assert.False(first.SelectionCan.CanMoveUp);
        Assert.True(first.SelectionCan.CanMoveDown);

        Assert.True(last.SelectionCan.CanMoveUp);
        Assert.False(last.SelectionCan.CanMoveDown);

        Assert.True(middle.SelectionCan.CanMoveUp);
        Assert.True(middle.SelectionCan.CanMoveDown);
    }

    [WinFormsFact]
    public void The_entries_are_greyed_by_what_the_window_says()
    {
        // The whole of the wiring, end to end: the engine works out that the top project has
        // nowhere above it, and the entry in the menu comes out greyed because of it.
        using var window = Window();

        var top = Commands.StateOf(AppCommand.MoveSelectionUp, window.NewContext(Node("a")));
        var below = Commands.StateOf(AppCommand.MoveSelectionUp, window.NewContext(Node("b")));

        Assert.False(top.Enabled);
        Assert.True(below.Enabled);
        Assert.Equal("Move project up", top.Label);
    }

    [WinFormsFact]
    public void A_row_the_window_knows_nothing_about_can_go_nowhere()
    {
        using var window = Window();

        var ghost = window.NewContext(Node("nowhere"));

        Assert.False(ghost.SelectionCan.CanMoveUp);
        Assert.False(ghost.SelectionCan.CanMoveDown);
    }

    private static SidebarNode Node(string id)
        => new(SidebarKind.Project, id, id, 0, SidebarKeys.For(SidebarKind.Project, id));

    private static MainForm Window()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "a", """{"id":"a","name":"A","child_order":1}""");
        store.PutResource("projects", "b", """{"id":"b","name":"B","child_order":2}""");
        store.PutResource("projects", "c", """{"id":"c","name":"C","child_order":3}""");

        return TestWindow.Build("termyn-sidebar-move.json", store);
    }
}
