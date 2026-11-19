using System.Reflection;
using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Several rows selected in the outline: what it reads back, what survives a sync, and what Shift
/// does with the keys.
/// </summary>
public class OutlineMultiSelectTests
{
    private const int VkShift = 0x10;
    private const int VkLeftShift = 0xA0;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmLeftDown = 0x0201;
    private const int WmLeftUp = 0x0202;
    private const int WmRightDown = 0x0204;
    private const int WmRightUp = 0x0205;
    private const int LeftButton = 0x0001;
    private const int RightButton = 0x0002;
    private const int ShiftHeld = 0x0004;

    private static TaskRow Row(string id) => new(id, id + " task", Priority.P4, "Work", string.Empty, []);

    private static TaskRow Heading(string said)
        => new($"day:{said}", said, Priority.P4, string.Empty, string.Empty, [], IsHeading: true);

    private static OutlineView Outline(params TaskRow[] rows)
    {
        var view = new OutlineView { Theme = Theme.Resolve(ThemePreference.Light) };
        view.CreateControl();
        view.Rows = rows;
        return view;
    }

    private static void Select(OutlineView view, params int[] indices)
    {
        foreach (var index in indices)
        {
            view.SelectedIndices.Add(index);
        }
    }

    /// <summary>Counts what the outline publishes while something happens to it.</summary>
    private static int Published(OutlineView view, Action change)
    {
        var seen = 0;
        void Record(object? sender, EventArgs e) => seen++;

        view.SelectedIndexChanged += Record;
        try
        {
            change();
        }
        finally
        {
            view.SelectedIndexChanged -= Record;
        }

        return seen;
    }

    // ---- What it reads back --------------------------------------------------------------------

    [WinFormsFact]
    public void Several_selected_are_read_back_top_to_bottom()
    {
        using var view = Outline(Row("a"), Row("b"), Row("c"));

        Select(view, 2, 0);

        Assert.Equal(["a", "c"], view.SelectedIds);
    }

    [WinFormsFact]
    public void With_several_selected_there_is_no_one_task_to_act_on()
    {
        // Everything that reads this is about to act on a single task, and picking one of the
        // several would be a guess.
        using var view = Outline(Row("a"), Row("b"), Row("c"));

        Select(view, 0, 1);

        Assert.Null(view.SelectedRow);
        Assert.Null(view.SelectedId);
    }

    [WinFormsFact]
    public void A_heading_taken_into_a_run_isnt_one_of_the_tasks_and_doesnt_break_the_run()
    {
        // Stepping off it the way a single selection does would throw the run away.
        using var view = Outline(Heading("1 Aug"), Row("a"), Row("b"), Heading("2 Aug"), Row("c"));

        Select(view, 2, 3, 4);

        Assert.Equal(["b", "c"], view.SelectedIds);
        Assert.Equal(3, view.SelectedIndices.Count);
    }

    [WinFormsFact]
    public void A_heading_added_at_the_top_of_a_run_isnt_stepped_off()
    {
        // Arriving on a heading alone moves the selection past it. Arriving on one with a task
        // already selected below it mustn't, or the task below is let go.
        using var view = Outline(Row("a"), Heading("2 Aug"), Row("c"));

        Select(view, 2, 1);

        Assert.Equal(2, view.SelectedIndices.Count);
        Assert.Equal(["c"], view.SelectedIds);
    }

    [WinFormsFact]
    public void One_task_and_a_heading_beside_it_are_still_that_one_task()
    {
        // The first press of Shift+↓ from a day's last task takes in the heading below it, and
        // nothing else yet.
        using var view = Outline(Row("a"), Heading("2 Aug"), Row("c"));

        Select(view, 0, 1);

        Assert.Equal("a", view.SelectedId);
    }

    [WinFormsFact]
    public void A_days_heading_isnt_opened_for_a_rename()
    {
        // The list's own F2 opens an edit over whichever row has the focus, and Shift+↓ onto a
        // heading leaves it there. What was typed then renamed the task selected above it.
        using var view = Outline(Row("a"), Heading("2 Aug"), Row("c"));
        using var window = Window(view);
        view.Items[1].Focused = true;

        view.Items[1].BeginEdit();
        Application.DoEvents();

        Assert.Equal(0, SendMessage(view.Handle, LvmGetEditControl, 0, 0));
    }

    /// <summary>LVM_GETEDITCONTROL, which hands back the box a rename is being typed in.</summary>
    private const int LvmGetEditControl = 0x1000 + 24;

    [WinFormsFact]
    public void Moving_the_selection_redraws_the_rows_it_left_and_reached_and_no_others()
    {
        // Redrawing the whole list for every arrow press repainted every row in view to move the
        // highlight by one.
        using var view = Outline(Enumerable.Range(0, 12).Select(i => Row($"r{i}")).ToArray());
        using var window = Window(view);
        view.SelectId("r2");
        var height = view.GetItemRect(0).Height;

        var redrawn = new List<Rectangle>();
        view.Invalidated += (_, e) => redrawn.Add(e.InvalidRect);
        view.SelectedIndices.Clear();
        view.SelectedIndices.Add(3);

        Assert.NotEmpty(redrawn);
        Assert.All(redrawn, r => Assert.True(r.Height <= height, $"{r} is more than a row"));
        Assert.Contains(redrawn, r => r.IntersectsWith(view.GetItemRect(2)));
        Assert.Contains(redrawn, r => r.IntersectsWith(view.GetItemRect(3)));
    }

    // ---- A sync landing ------------------------------------------------------------------------

    [WinFormsFact]
    public void A_refresh_keeps_every_selected_task_selected()
    {
        using var view = Outline(Row("a"), Row("b"), Row("c"));
        Select(view, 0, 2);

        view.Rows = [Row("new"), Row("a"), Row("b"), Row("c")];

        Assert.Equal(["a", "c"], view.SelectedIds);
    }

    [WinFormsFact]
    public void A_refresh_lets_go_of_only_the_task_that_went()
    {
        using var view = Outline(Row("a"), Row("b"), Row("c"));
        Select(view, 0, 2);

        var seen = Published(view, () => view.Rows = [Row("a"), Row("b")]);

        Assert.Equal(["a"], view.SelectedIds);
        Assert.Equal(1, seen);
    }

    [WinFormsFact]
    public void A_refresh_that_moves_nothing_says_nothing_with_several_selected()
    {
        using var view = Outline(Row("a"), Row("b"), Row("c"));
        Select(view, 0, 2);

        Assert.Equal(0, Published(view, () => view.Rows = [Row("a"), Row("b"), Row("c")]));
    }

    [WinFormsFact]
    public void A_task_the_server_renames_stays_selected_with_the_rest()
    {
        using var view = Outline(Row("t-1"), Row("b"));
        view.Renamed = id => id == "t-1" ? "real" : id;
        Select(view, 0, 1);

        view.Rows = [Row("real"), Row("b")];

        Assert.Equal(["real", "b"], view.SelectedIds);
    }

    // ---- A long run ----------------------------------------------------------------------------

    /// <summary>
    /// A loose ceiling for a refresh under a run Shift took over a whole view, as a regression
    /// gate rather than a measurement: there to catch the day finding the selection again turns
    /// quadratic, which takes seconds at these sizes, not to time the machine.
    /// </summary>
    private static readonly TimeSpan RefreshCeiling = TimeSpan.FromMilliseconds(500);

    [WinFormsFact]
    public void A_refresh_under_thousands_of_selected_tasks_finds_each_without_a_walk_of_the_rows()
    {
        // Each task's row was found by walking the rows from the top, and a refresh comes with
        // every write and every sync.
        var rows = Enumerable.Range(0, 20_000).Select(i => Row($"r{i}")).ToArray();
        using var view = Outline(rows);
        using var window = Window(view);
        view.SelectId("r0");
        PressWithShift(view, VkEnd);
        Assert.Equal(20_000, view.SelectedIds.Count);

        var best = Best(rounds: 3, () => view.Rows = rows.ToList());

        Assert.Equal(20_000, view.SelectedIds.Count);
        Assert.True(best < RefreshCeiling, $"a refresh under 20,000 selected took {best.TotalMilliseconds:N0} ms");
    }

    [WinFormsFact]
    public void A_refresh_that_moves_thousands_of_selected_tasks_reads_the_selection_once()
    {
        // Every task moves a row when one arrives above them or goes, so each is put back in turn.
        // The selection was read whole for every one of them.
        var rows = Enumerable.Range(0, 4_000).Select(i => Row($"r{i}")).ToArray();
        using var view = Outline(rows);
        using var window = Window(view);
        view.SelectId("r0");
        PressWithShift(view, VkEnd);

        // A task arriving above them and going again, so every round moves them all. Taking one of
        // them away instead lets the list drop the last index by itself, and the rest then line up
        // with no moving at all.
        var above = rows.Prepend(Row("above")).ToList();
        var round = 0;
        var best = Best(rounds: 4, () => view.Rows = round++ % 2 == 0 ? above : rows.ToList());

        Assert.Equal(4_000, view.SelectedIds.Count);
        Assert.True(best < RefreshCeiling, $"moving 4,000 selected took {best.TotalMilliseconds:N0} ms");
    }

    /// <summary>The quickest of several goes at something.</summary>
    private static TimeSpan Best(int rounds, Action act)
    {
        var best = TimeSpan.MaxValue;
        for (var round = 0; round < rounds; round++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            act();
            watch.Stop();

            if (watch.Elapsed < best) best = watch.Elapsed;
        }

        return best;
    }

    private const int VkEnd = 0x23;

    // ---- The keys ------------------------------------------------------------------------------

    [WinFormsFact]
    public void Shift_and_an_arrow_takes_in_the_next_task()
    {
        using var view = Outline(Row("a"), Row("b"), Row("c"));
        using var window = Window(view);
        view.SelectId("a");

        PressWithShift(view, VkDown);

        Assert.Equal(["a", "b"], view.SelectedIds);
    }

    [WinFormsFact]
    public void Shift_and_an_arrow_carries_a_run_across_a_days_heading()
    {
        using var view = Outline(Row("a"), Heading("2 Aug"), Row("c"));
        using var window = Window(view);
        view.SelectId("a");

        PressWithShift(view, VkDown);
        PressWithShift(view, VkDown);

        Assert.Equal(["a", "c"], view.SelectedIds);
    }

    [WinFormsFact]
    public void Shift_and_an_arrow_up_onto_a_heading_keeps_the_run()
    {
        // The heading is the top of the run then, which a single selection would be stepped off.
        using var view = Outline(Row("a"), Heading("2 Aug"), Row("c"));
        using var window = Window(view);
        view.SelectId("c");

        PressWithShift(view, VkUp);
        Assert.Equal(2, view.SelectedIndices.Count);

        PressWithShift(view, VkUp);
        Assert.Equal(["a", "c"], view.SelectedIds);
    }

    // ---- Where a Shift run starts --------------------------------------------------------------

    [WinFormsFact]
    public void Shift_after_a_task_is_picked_out_from_code_runs_from_that_task()
    {
        // The list measures a run from the row it last saw clicked or arrowed onto. A task picked
        // out by the window — a new sub-task, a search, a move — left that behind, and Shift+↓ took
        // in every task between the two.
        using var view = Outline(Row("a"), Row("b"), Row("c"), Row("d"), Row("e"));
        using var window = Window(view);
        view.SelectId("a");
        Press(view, VkDown);

        view.SelectId("d");
        PressWithShift(view, VkDown);

        Assert.Equal(["d", "e"], view.SelectedIds);
    }

    [WinFormsFact]
    public void A_refresh_keeps_where_a_Shift_run_started()
    {
        // A task ticked off above the run leaves, every row moves up one, and the list's mark stayed
        // on the old index — the next Shift press let go of the task the run started from.
        using var view = Outline(Row("a"), Row("b"), Row("c"), Row("d"));
        using var window = Window(view);
        view.SelectId("b");
        PressWithShift(view, VkDown);

        view.Rows = [Row("b"), Row("c"), Row("d")];
        PressWithShift(view, VkDown);

        Assert.Equal(["b", "c", "d"], view.SelectedIds);
    }

    [WinFormsFact]
    public void Shift_after_an_arrow_steps_past_a_heading_runs_from_the_task_it_landed_on()
    {
        // Left on the heading, Shift+↑ ran from the heading to itself and selected no task at all.
        using var view = Outline(Row("a"), Heading("2 Aug"), Row("c"), Row("d"));
        using var window = Window(view);
        view.SelectId("a");
        Press(view, VkDown);
        Assert.Equal("c", view.SelectedId);

        PressWithShift(view, VkUp);

        Assert.Equal(["c"], view.SelectedIds);
    }

    // ---- Letting go ----------------------------------------------------------------------------

    [WinFormsFact]
    public void Letting_go_of_the_task_beside_a_heading_selects_nothing_rather_than_another_task()
    {
        // Ctrl+click on the last task of a run that ends on a heading leaves only the heading. It
        // didn't arrive there by an arrow, so there's no way it was going to carry on.
        using var view = Outline(Row("a"), Heading("2 Aug"), Row("c"));
        Select(view, 2, 1);

        view.SelectedIndices.Remove(2);

        Assert.Empty(view.SelectedIds);
    }

    [WinFormsFact]
    public void A_plain_arrow_from_several_onto_a_heading_still_steps_past_it()
    {
        using var view = Outline(Row("a"), Row("b"), Heading("2 Aug"), Row("c"));
        using var window = Window(view);
        view.SelectId("a");
        PressWithShift(view, VkDown);

        Press(view, VkDown);

        Assert.Equal(["c"], view.SelectedIds);
    }

    // ---- The mouse -----------------------------------------------------------------------------

    [WinFormsFact]
    public void Shift_and_a_click_takes_in_the_run_and_says_so()
    {
        // A run taken in by a click arrives as one change to a range of rows rather than as a
        // change of selection, and whatever follows the selection has to hear about it all the same.
        using var view = Outline(Row("a"), Row("b"), Row("c"), Row("d"));
        using var window = Window(view);
        view.SelectId("a");

        var said = new List<IReadOnlyList<string>>();
        view.SelectedIndexChanged += (_, _) => said.Add(view.SelectedIds);

        ShiftClick(view, 3);

        Assert.Equal(["a", "b", "c", "d"], view.SelectedIds);
        Assert.Equal(["a", "b", "c", "d"], said[^1]);
    }

    [WinFormsFact]
    public void A_drag_turned_away_from_one_of_several_selects_that_one_alone()
    {
        // The list holds back selecting the pressed row alone until it knows no drag is starting.
        // A drag of several is turned away, and the list never got round to it, so a click that
        // moved a few pixels left every task selected for the next Delete to take.
        //
        // Started by hand, as the list's drag notice would. The list decides a drag has begun by
        // watching the real mouse, so posted messages never get it that far.
        using var view = Outline(Row("a"), Row("b"), Row("c"));
        using var window = Window(view);
        Select(view, 0, 1, 2);

        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(OutlineView).GetField("_pressedTask", Hidden)!.SetValue(view, "b");
        typeof(ListView).GetMethod("OnItemDrag", Hidden)!.Invoke(view, [new ItemDragEventArgs(MouseButtons.Left, view.Items[1])]);

        Assert.Equal(["b"], view.SelectedIds);
    }

    /// <summary>A press and release of the left button over a row, with Shift held.</summary>
    private static void ShiftClick(OutlineView view, int index)
    {
        var row = view.GetItemRect(index);
        var at = (nint)(((row.Top + (row.Height / 2)) << 16) | ((row.Left + 60) & 0xFFFF));

        var state = new byte[256];
        state[VkShift] = state[VkLeftShift] = 0x80;

        try
        {
            GetKeyState(VkShift);
            SetKeyboardState(state);

            PostMessage(view.Handle, WmLeftDown, LeftButton | ShiftHeld, at);
            PostMessage(view.Handle, WmLeftUp, ShiftHeld, at);

            for (var round = 0; round < 5; round++)
            {
                Application.DoEvents();
            }
        }
        finally
        {
            GetKeyState(VkShift);
            SetKeyboardState(new byte[256]);
        }
    }

    [WinFormsFact]
    public void A_right_click_on_one_of_several_keeps_them_all()
    {
        // The menu that follows is about the lot, the way Explorer's is over several files.
        using var view = Outline(Row("a"), Row("b"), Row("c"));
        using var window = Window(view);
        Select(view, 0, 1);

        RightClick(view, 1);

        Assert.Equal(["a", "b"], view.SelectedIds);
    }

    [WinFormsFact]
    public void A_right_click_on_a_task_outside_them_selects_that_one_alone()
    {
        using var view = Outline(Row("a"), Row("b"), Row("c"));
        using var window = Window(view);
        Select(view, 0, 1);

        RightClick(view, 2);

        Assert.Equal(["c"], view.SelectedIds);
    }

    [WinFormsFact]
    public void A_right_click_on_a_task_outside_them_moves_where_a_Shift_run_starts()
    {
        // The native list does this itself, as it does for a left click, so nothing here has to.
        // Checked because the selection made on the press would leave the mark behind if it ever
        // stopped. Read off the list rather than pressed: the click gives the list the focus, and
        // a key pressed at a focused list could have come from whoever's at the machine.
        using var view = Outline(Row("a"), Row("b"), Row("c"), Row("d"));
        using var window = Window(view);
        view.SelectId("a");
        Press(view, VkDown);

        RightClick(view, 2);

        Assert.Equal(2, (int)SendMessage(view.Handle, LvmGetSelectionMark, 0, 0));
    }

    /// <summary>LVM_GETSELECTIONMARK, the row the list runs a Shift selection from.</summary>
    private const int LvmGetSelectionMark = 0x1000 + 66;

    /// <summary>
    /// A press and release of the right button over a row, as the messages a mouse sends.
    /// </summary>
    /// <remarks>
    /// Both are posted before either is pumped, for the reason <see cref="RealMouse"/> gives: the
    /// list waits inside its own loop for the release.
    /// </remarks>
    private static void RightClick(OutlineView view, int index)
    {
        var row = view.GetItemRect(index);
        var at = (nint)(((row.Top + (row.Height / 2)) << 16) | ((row.Left + 60) & 0xFFFF));

        PostMessage(view.Handle, WmRightDown, RightButton, at);
        PostMessage(view.Handle, WmRightUp, 0, at);

        for (var round = 0; round < 5; round++)
        {
            Application.DoEvents();
        }
    }

    /// <summary>Presses a key on the list with Shift held, and nothing else.</summary>
    /// <param name="view">The list to press it on</param>
    /// <param name="key">Which key, as Windows numbers them</param>
    private static void PressWithShift(OutlineView view, int key) => Press(view, key, shift: true);

    /// <summary>
    /// Presses a key on the list, with Shift held or with nothing held at all.
    /// </summary>
    /// <remarks>
    /// The list reads Shift off this thread's key state, which follows the machine's real keyboard.
    /// One key is asked about before the state is set, so a change still on its way from another
    /// window lands then rather than on the list's own question.
    /// </remarks>
    /// <param name="view">The list to press it on</param>
    /// <param name="key">Which key, as Windows numbers them</param>
    /// <param name="shift">Whether Shift is held</param>
    private static void Press(OutlineView view, int key, bool shift = false)
    {
        Assert.True(
            GetActiveWindow() != view.FindForm()?.Handle && !view.Focused,
            "The list's window has been activated, so anything typed elsewhere could reach it.");

        var state = new byte[256];
        if (shift) state[VkShift] = state[VkLeftShift] = 0x80;

        try
        {
            GetKeyState(VkShift);
            SetKeyboardState(state);

            SendMessage(view.Handle, WmKeyDown, key, 0);
            SendMessage(view.Handle, WmKeyUp, key, unchecked((int)0xC0000001));
            Application.DoEvents();
        }
        finally
        {
            GetKeyState(VkShift);
            SetKeyboardState(new byte[256]);
        }
    }

    /// <summary>
    /// Puts the outline in a window of its own, off-screen and never brought to the front, so
    /// nothing typed elsewhere while these run reaches it.
    /// </summary>
    private static Form Window(OutlineView view)
    {
        var form = new InactiveForm { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(600, 400) };
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();

        return form;
    }

    /// <summary>A window that doesn't activate on being shown.</summary>
    private sealed class InactiveForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();
}
