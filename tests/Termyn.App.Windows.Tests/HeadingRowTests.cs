using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// A day's heading in the outline: a row the list holds like any other, which nothing may treat
/// as a task.
/// </summary>
/// <remarks>
/// The selection is where this goes wrong. A heading sits between the tasks, so every arrow key
/// and every click can land on one, and a command that took it would act on a row with no task
/// behind it.
/// </remarks>
public class HeadingRowTests
{
    /// <summary>Two days, each headed, with two tasks under the first and one under the second.</summary>
    private static OutlineView Outline()
    {
        var outline = new OutlineView();
        outline.CreateControl();

        outline.Rows =
        [
            Heading("1 Aug · Tomorrow"),
            Task("a", "First"),
            Task("b", "Second"),
            Heading("2 Aug · Sunday"),
            Task("c", "Third"),
            Task("d", "Fourth"),
        ];

        return outline;
    }

    private static TaskRow Heading(string said)
        => new($"day:{said}", said, Priority.P4, string.Empty, string.Empty, [], IsHeading: true);

    private static TaskRow Task(string id, string content)
        => new(id, content, Priority.P4, "Work", string.Empty, []);

    private static void Select(OutlineView outline, int index)
    {
        outline.SelectedIndices.Clear();
        outline.SelectedIndices.Add(index);
    }

    [WinFormsFact]
    public void Arriving_on_a_heading_from_above_carries_on_down()
    {
        using var outline = Outline();

        Select(outline, 2);   // "Second", the row above the second heading
        Select(outline, 3);   // the heading itself, as ↓ would reach it

        Assert.Equal("Third", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void Arriving_on_a_heading_from_below_carries_on_up()
    {
        using var outline = Outline();

        Select(outline, 4);   // "Third"
        Select(outline, 3);   // the heading above it, as ↑ would reach it

        Assert.Equal("Second", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void The_heading_at_the_top_hands_the_selection_down_whichever_way_it_came()
    {
        // Nothing above it to carry on to, and the list starts on one in Upcoming — so arriving
        // there from below has to turn round rather than leave the selection on a heading.
        using var outline = Outline();

        Select(outline, 1);
        Select(outline, 0);

        Assert.Equal("First", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void A_heading_is_never_what_the_window_is_told_is_selected()
    {
        using var outline = Outline();

        Select(outline, 3);

        Assert.NotNull(outline.SelectedRow);
        Assert.False(outline.SelectedRow?.IsHeading);
        Assert.NotEqual("day:2 Aug · Sunday", outline.SelectedId);
    }

    [WinFormsFact]
    public void Stepping_past_a_heading_is_no_noisier_than_an_ordinary_move()
    {
        // The step means clearing an index and adding another, and each of those is an event of
        // its own. Held quiet, so landing on a heading says no more about the selection than
        // landing on the task below it would.
        using var outline = Outline();

        Assert.Equal(Moves(outline, from: 1, to: 2), Moves(outline, from: 2, to: 3));
        Assert.Equal("Third", outline.SelectedRow?.Content);

        static int Moves(OutlineView outline, int from, int to)
        {
            Select(outline, from);

            var said = 0;
            void Count(object? sender, EventArgs e) => said++;

            outline.SelectedIndexChanged += Count;
            Select(outline, to);
            outline.SelectedIndexChanged -= Count;

            return said;
        }
    }

    [WinFormsFact]
    public void Which_way_the_selection_was_going_is_forgotten_with_the_rows()
    {
        // A sync replaces these every 45 seconds. An index left over from the last lot names a
        // different task, and the step off a heading would be sent the wrong way by it.
        using var outline = Outline();

        Select(outline, 4);   // travelling up, as far as the old rows were concerned
        outline.Rows = outline.Rows.ToList();

        Select(outline, 3);

        Assert.Equal("Third", outline.SelectedRow?.Content);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    private const int WmKeyDown = 0x0100;
    private const int VkMenu = 0x12;

    /// <summary>
    /// Presses a key on the list itself, so its own navigation does the moving.
    /// </summary>
    /// <remarks>
    /// With nothing else held. The list asks whether Alt is down, and doesn't move on an arrow
    /// that comes with it — and this thread's key state follows the machine's real keyboard, so an
    /// Alt+Tab or an AltGr character typed in another window while these ran was enough to fail
    /// them. Ctrl and Shift don't matter here: they only change what an arrow does in a list that
    /// can select more than one row, and this one can't.
    ///
    /// Alt is asked about before the state is cleared. A key pressed or let go anywhere reaches
    /// the thread the next time it asks about one, over the top of anything set in between, so
    /// cleared without that, a change still on its way would land on the list's own question.
    ///
    /// And the window mustn't have been activated, which <see cref="Window"/> keeps it from and a
    /// test giving the list the focus would undo. Asked of this thread rather than of the desktop:
    /// whether an activated window gets the foreground depends on the machine at that moment, so
    /// a check on the foreground would miss one whenever Windows refused it the switch.
    /// </remarks>
    /// <param name="outline">The list to press it on</param>
    /// <param name="key">Which key, as Windows numbers them</param>
    private static void Press(OutlineView outline, int key)
    {
        Assert.True(
            GetActiveWindow() != outline.FindForm()?.Handle && !outline.Focused,
            "The list's window has been activated, so anything typed elsewhere could reach it.");

        GetKeyState(VkMenu);
        SetKeyboardState(new byte[256]);

        SendMessage(outline.Handle, WmKeyDown, key, 0);
        Application.DoEvents();
    }

    /// <summary>
    /// Puts the outline in a window of its own, off-screen and never brought to the front.
    /// </summary>
    /// <remarks>
    /// A window that came to the front would take whatever anyone was typing elsewhere: the keys
    /// would arrive on this thread, the pump in <see cref="Press"/> would hand them to the list,
    /// and the list would move on them. So it's shown without being activated, and the list isn't
    /// given the focus either, since asking for that activates the window as well. It doesn't need
    /// the focus: <see cref="Press"/> sends the key to the list directly.
    /// </remarks>
    /// <param name="outline">The list to put in it</param>
    /// <returns>The window, shown, which the caller disposes</returns>
    private static Form Window(OutlineView outline)
    {
        var form = new InactiveForm { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(600, 400) };
        outline.Dock = DockStyle.Fill;
        form.Controls.Add(outline);
        form.Show();

        return form;
    }

    /// <summary>A window that doesn't activate on being shown.</summary>
    private sealed class InactiveForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [WinFormsTheory]
    [InlineData(0x28, "b", "Third", "Fourth")]   // down from a day's last task, across, and on again
    [InlineData(0x26, "c", "Second", "First")]   // and the same going up
    public void Crossing_a_heading_doesnt_swallow_the_next_keypress(int key, string from, string across, string andOn)
    {
        // Two presses, because the first one is fine either way: a list moves its selection from
        // wherever its focus is, so moving only the selection off a heading leaves the focus
        // sitting on it — and the press after that steps the focus onto the row already selected.
        // Nothing moves, and to anyone pressing the key it reads as a keystroke thrown away.
        using var outline = Outline();
        using var form = Window(outline);

        outline.SelectId(from);

        Press(outline, key);
        Assert.Equal(across, outline.SelectedRow?.Content);

        Press(outline, key);
        Assert.Equal(andOn, outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void A_task_picked_out_from_elsewhere_can_be_stepped_off_straight_away()
    {
        // The same quirk as crossing a heading, met from the other side: a row selected by a
        // search, the palette or the tray takes the focus with it, or the first arrow key
        // afterwards only brings the focus back and nothing appears to happen.
        using var outline = Outline();
        using var form = Window(outline);

        outline.SelectId("a");

        Press(outline, 0x28);

        Assert.Equal("Second", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void An_alt_held_in_another_window_doesnt_reach_a_press()
    {
        // Left down in this thread's key state the way an Alt+Tab elsewhere leaves it. The list
        // doesn't move on an arrow that comes with Alt, so a press that let it through would fail
        // the tests above for a reason nothing in the repository could account for.
        using var outline = Outline();
        using var form = Window(outline);
        outline.SelectId("a");

        var held = new byte[256];
        held[VkMenu] = 0x80;
        GetKeyState(VkMenu);
        SetKeyboardState(held);
        Assert.True(GetKeyState(VkMenu) < 0, "Alt didn't take, so this would pass whatever Press did.");

        // And the list still ignores an arrow that comes with Alt. If a later Windows stopped
        // doing that, this would pass whether Press cleared anything or not.
        SendMessage(outline.Handle, WmKeyDown, 0x28, 0);
        Application.DoEvents();
        Assert.Equal("First", outline.SelectedRow?.Content);

        Press(outline, 0x28);

        Assert.Equal("Second", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void The_list_knows_a_heading_by_where_it_is_on_screen()
    {
        // What a click is turned away by. Dropping the click where it lands is the whole of the
        // fix for the flash: letting the list select a heading and moving the selection off again
        // lights up the row next to it and puts it out, on a row nobody clicked.
        using var outline = Outline();
        outline.Size = new Size(400, 300);

        var heading = outline.GetItemRect(3);
        var task = outline.GetItemRect(4);

        Assert.True(outline.IsHeadingAt(new Point(heading.Left + 20, heading.Top + (heading.Height / 2))));
        Assert.False(outline.IsHeadingAt(new Point(task.Left + 20, task.Top + (task.Height / 2))));
    }

    [WinFormsFact]
    public void A_heading_hands_over_its_day_and_nothing_else()
    {
        // What the control is given as the row's cells, which is what a screen reader reads out.
        using var outline = Outline();

        Assert.Equal(
            ["1 Aug · Tomorrow", "", "", "", "", ""],
            outline.Cells(outline.Rows[0]));
    }
}
