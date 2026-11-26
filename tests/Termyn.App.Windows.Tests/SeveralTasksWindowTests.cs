using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The window's half of acting on several tasks at once: which commands reach the lot, which are
/// turned away, and what the menu and the panel say over them.
/// </summary>
/// <remarks>
/// The middle of it, which neither end can see. The outline reads back its rows and the presenter
/// acts on whatever ids it's given, and both are right whether or not the window hands the one the
/// other's selection.
/// </remarks>
public class SeveralTasksWindowTests
{
    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"Ship it","project_id":"p1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Something else","project_id":"p1","child_order":2}""");
        store.PutResource("items", "c", """{"id":"c","content":"Leave this one","project_id":"p1","child_order":3}""");
        return store;
    }

    /// <summary>A parent with two sub-tasks under it, and a task beside it.</summary>
    private static InMemorySnapshotStore Family()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "p", """{"id":"p","content":"Parent","project_id":"p1","child_order":1}""");
        store.PutResource("items", "c1", """{"id":"c1","content":"First under it","project_id":"p1","parent_id":"p","child_order":1}""");
        store.PutResource("items", "c2", """{"id":"c2","content":"Second under it","project_id":"p1","parent_id":"p","child_order":2}""");
        store.PutResource("items", "o", """{"id":"o","content":"Beside it","project_id":"p1","child_order":2}""");
        return store;
    }

    /// <summary>A window over the account, showing every task, with the given ones selected.</summary>
    private static MainForm Window(string name, out MainPresenter presenter, params string[] selected)
        => Window(name, Account(), out presenter, selected);

    /// <summary>A window over an account of the caller's, showing every task, with the given ones selected.</summary>
    private static MainForm Window(string name, InMemorySnapshotStore store, out MainPresenter presenter, params string[] selected)
    {
        var window = TestWindow.Build(name, store, out _, out presenter);
        TestWindow.ShowOffScreen(window);
        presenter.Select(ViewSelection.Of(SmartView.All));

        var outline = TestWindow.Find<OutlineView>(window);
        foreach (var id in selected)
        {
            outline.SelectedIndices.Add(outline.RowOf(id));
        }

        return window;
    }

    [WinFormsFact]
    public void Delete_takes_every_task_selected_and_one_undo_brings_them_all_back()
    {
        using var window = Window("several-delete.json", out var presenter, "a", "b");

        window.Run(AppCommand.Delete);
        Assert.Equal(["c"], presenter.Rows.Select(r => r.Id));

        window.Run(AppCommand.Undo);
        Assert.Equal(["Leave this one", "Ship it", "Something else"], presenter.Rows.Select(r => r.Content).Order());
    }

    [WinFormsFact]
    public void Ticking_off_a_mix_ticks_off_the_open_ones_and_leaves_the_done_one_done()
    {
        // Kept on the list for a moment after it's ticked off, which is how a done task and an
        // open one come to be selected together.
        using var window = Window("several-mix.json", out var presenter);
        presenter.Complete("a");

        var outline = TestWindow.Find<OutlineView>(window);
        outline.SelectedIndices.Add(outline.RowOf("a"));
        outline.SelectedIndices.Add(outline.RowOf("b"));

        window.Run(AppCommand.ToggleComplete);

        Assert.True(presenter.Rows.Single(r => r.Id == "a").Completed);
        Assert.True(presenter.Rows.Single(r => r.Id == "b").Completed);
        Assert.False(presenter.Rows.Single(r => r.Id == "c").Completed);
    }

    [WinFormsFact]
    public void A_priority_goes_on_every_task_selected()
    {
        using var window = Window("several-priority.json", out var presenter, "a", "c");

        window.Run(AppCommand.Priority1);

        Assert.Equal(
            [("a", Priority.P1), ("b", Priority.P4), ("c", Priority.P1)],
            presenter.Rows.Select(r => (r.Id, r.Priority)).OrderBy(r => r.Id));
    }

    [WinFormsFact]
    public void A_command_for_one_task_says_to_pick_one_and_does_nothing()
    {
        using var window = Window("several-one-only.json", out var presenter, "a", "b");

        window.Run(AppCommand.DueToday);

        Assert.Equal(Strings.OneTaskOnly, TestWindow.StatusLine(window).Text);
        Assert.All(presenter.Rows, r => Assert.Null(r.DueOn));
    }

    [WinFormsFact]
    public void The_lists_own_rename_on_F2_is_turned_away_with_several_selected()
    {
        // The list starts an edit by itself on F2, on whichever row has the focus, without asking
        // the window's key table first.
        using var window = Window("several-rename.json", out _, "a", "b");
        var outline = TestWindow.Find<OutlineView>(window);

        outline.Items[outline.RowOf("a")].BeginEdit();
        Application.DoEvents();

        Assert.Equal(0, SendMessage(outline.Handle, LvmGetEditControl, 0, 0));
        Assert.Equal(Strings.OneTaskOnly, TestWindow.StatusLine(window).Text);
    }

    [WinFormsFact]
    public void A_rename_goes_to_the_row_its_box_is_over_rather_than_the_one_selected()
    {
        // The list's own F2 opens the box over whichever row has the focus, and Ctrl+click leaves
        // the focus on a task it's just let go of. What was typed over that one renamed the task
        // still selected.
        using var window = Window("several-rename-focus.json", out var presenter, "a");
        var outline = TestWindow.Find<OutlineView>(window);
        var other = outline.RowOf("b");
        outline.Items[other].Focused = true;

        outline.Items[other].BeginEdit();
        Application.DoEvents();
        var box = SendMessage(outline.Handle, LvmGetEditControl, 0, 0);
        Assert.NotEqual(0, box);

        SendMessage(box, WmSetText, 0, "Renamed");
        SendMessage(box, WmKeyDown, VkReturn, 0);
        Application.DoEvents();

        Assert.Equal("Renamed", presenter.Rows.Single(r => r.Id == "b").Content);
        Assert.Equal("Ship it", presenter.Rows.Single(r => r.Id == "a").Content);
    }

    [WinFormsFact]
    public void Several_folded_away_together_leave_the_task_they_were_folded_into_selected()
    {
        // With one selected, a fold that hid it moved the selection to where it went. With
        // several, the selection was left with nothing in it and the panel went blank.
        using var window = Window("several-fold.json", Family(), out _, "c1", "c2");
        var outline = TestWindow.Find<OutlineView>(window);

        window.Run(AppCommand.CollapseAll);

        Assert.Equal(["p"], outline.SelectedIds);
    }

    /// <summary>LVM_GETEDITCONTROL, which hands back the box a rename is being typed in.</summary>
    private const int LvmGetEditControl = 0x1000 + 24;

    private const int WmSetText = 0x000C;
    private const int WmKeyDown = 0x0100;
    private const int VkReturn = 0x0D;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, string lParam);

    [WinFormsFact]
    public void The_task_menu_over_several_offers_what_acts_on_several_and_greys_the_rest()
    {
        using var window = Window("several-menu.json", out _, "a", "b");
        var outline = TestWindow.Find<OutlineView>(window);

        Assert.True(window.OpenTaskMenu());

        var items = Every(outline.ContextMenuStrip!.Items).ToDictionary(i => i.Text ?? string.Empty);
        Assert.True(items["Delete"].Enabled);
        Assert.True(items["Complete"].Enabled);
        Assert.False(items["Rename"].Enabled);
    }

    [WinFormsFact]
    public void The_panel_says_how_many_are_selected()
    {
        using var window = Window("several-panel.json", out _, "a", "b", "c");

        Assert.Equal("3 tasks selected", TestWindow.Find<DetailHeader>(window).Subject);
    }

    /// <summary>Every entry in a menu, submenus included.</summary>
    private static IEnumerable<ToolStripMenuItem> Every(ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            yield return item;

            foreach (var child in Every(item.DropDownItems))
            {
                yield return child;
            }
        }
    }
}
