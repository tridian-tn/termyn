using System.Diagnostics;
using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The tip that says what a row's priority dot means.
/// </summary>
/// <remarks>
/// The pause is waited out for real, and presses, leaving and the wheel arrive as the messages
/// Windows sends. The pointer's put on a dot through the list rather than by a posted move:
/// Windows answers a move to a window the real pointer isn't over by saying it's left again,
/// which takes the tip straight back down. Whether the tip window is then drawn isn't asked —
/// it's only drawn for the window in front, and a test host's can't take the front.
/// </remarks>
public class PriorityTipTests
{
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseLeave = 0x02A3;

    private static TaskRow Task(string id, Priority priority)
        => new(id, $"Task {id}", priority, "Work", string.Empty, []);

    private static TaskRow Heading(string said)
        => new($"day:{said}", said, Priority.P4, string.Empty, string.Empty, [], IsHeading: true);

    private static Form Window()
        => new() { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(700, 400) };

    /// <summary>A list with a task at each priority, a day's heading, and a second urgent task.</summary>
    private static OutlineView Outline(Form form)
    {
        var outline = new OutlineView { Dock = DockStyle.Fill };
        form.Controls.Add(outline);
        form.Show();

        outline.Rows =
        [
            Task("a", Priority.P1),
            Task("b", Priority.P2),
            Task("c", Priority.P3),
            Task("d", Priority.P4),
            Heading("1 Aug · Tomorrow"),
            Task("e", Priority.P1),
        ];

        return outline;
    }

    /// <summary>
    /// The middle of a row's priority cell, found from the columns rather than by counting pixels.
    /// </summary>
    private static Point PriorityCellOf(OutlineView outline, int index)
    {
        var row = outline.GetItemRect(index);
        var left = row.Left;

        foreach (ColumnHeader header in outline.Columns)
        {
            if (header.Tag is TaskColumn.Priority)
                return new Point(left + (header.Width / 2), row.Top + (row.Height / 2));

            left += header.Width;
        }

        throw new InvalidOperationException("the outline has no priority column");
    }

    /// <summary>Lets the window run for long enough that the first pause has run out.</summary>
    private static void Rest() => Wait(1200);

    /// <summary>Lets the window run for a while, so a timer that's due can fire.</summary>
    private static void Wait(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static nint Position(Point at) => (at.Y << 16) | (at.X & 0xFFFF);

    [WinFormsFact]
    public void Each_dot_says_its_priority()
    {
        using var form = Window();
        using var outline = Outline(form);

        Assert.Equal("Priority 1 — urgent", outline.PriorityTipAt(PriorityCellOf(outline, 0)));
        Assert.Equal("Priority 2 — high", outline.PriorityTipAt(PriorityCellOf(outline, 1)));
        Assert.Equal("Priority 3 — medium", outline.PriorityTipAt(PriorityCellOf(outline, 2)));
    }

    [WinFormsFact]
    public void Nothing_is_said_where_no_dot_is_drawn()
    {
        // A task with no priority gets no dot, and a tip over an empty cell would be describing
        // something nobody can see. A heading has no priority to describe.
        using var form = Window();
        using var outline = Outline(form);

        Assert.Null(outline.PriorityTipAt(PriorityCellOf(outline, 3)));
        Assert.Null(outline.PriorityTipAt(PriorityCellOf(outline, 4)));
    }

    [WinFormsFact]
    public void Only_the_priority_column_answers()
    {
        // The task's name, a little way into its row, isn't the dot.
        using var form = Window();
        using var outline = Outline(form);

        var row = outline.GetItemRect(0);

        Assert.Null(outline.PriorityTipAt(new Point(row.Left + 60, row.Top + (row.Height / 2))));
    }

    [WinFormsFact]
    public void The_whole_cell_answers_not_just_the_dot()
    {
        // The dot's nine pixels across. A tip that only came up on those would be one people
        // didn't know was there.
        using var form = Window();
        using var outline = Outline(form);

        var middle = PriorityCellOf(outline, 0);
        var across = 0;

        for (var x = middle.X - 30; x < middle.X + 30; x++)
            if (outline.PriorityTipAt(new Point(x, middle.Y)) is not null)
                across++;

        Assert.True(across >= 16, $"the tip answers across {across}px");
    }

    [WinFormsFact]
    public void A_mouse_move_points_the_tip_at_the_dot_under_it()
    {
        // Sent rather than posted, and read before anything's pumped, so the leave Windows queues
        // behind it hasn't been handled yet.
        using var form = Window();
        using var outline = Outline(form);

        SendMessage(outline.Handle, WmMouseMove, 0, Position(PriorityCellOf(outline, 0)));

        Assert.Equal("Priority 1 — urgent", outline.TipAimedAt);
    }

    [WinFormsFact]
    public void Resting_on_a_dot_brings_the_tip_up_after_a_pause()
    {
        using var form = Window();
        using var outline = Outline(form);

        outline.PointerAt(PriorityCellOf(outline, 0));
        Assert.Null(outline.TipShowing);   // not straight away: that's what the pause is for

        Rest();
        Assert.Equal("Priority 1 — urgent", outline.TipShowing);
    }

    [WinFormsFact]
    public void Moving_on_to_another_rows_dot_shows_that_rows_priority()
    {
        // With the whole list one tip as far as Windows was concerned, the first row's words
        // stayed up wherever the pointer went next.
        using var form = Window();
        using var outline = Outline(form);

        outline.PointerAt(PriorityCellOf(outline, 0));
        Rest();
        Assert.Equal("Priority 1 — urgent", outline.TipShowing);

        outline.PointerAt(PriorityCellOf(outline, 1));
        Rest();
        Assert.Equal("Priority 2 — high", outline.TipShowing);
    }

    [WinFormsFact]
    public void Another_rows_dot_counts_as_new_even_when_it_says_the_same()
    {
        // The tip comes down and back up by the new row, rather than staying beside the old one.
        using var form = Window();
        using var outline = Outline(form);

        outline.PointerAt(PriorityCellOf(outline, 0));
        Rest();
        Assert.Equal("Priority 1 — urgent", outline.TipShowing);

        outline.PointerAt(PriorityCellOf(outline, 5));
        Assert.Null(outline.TipShowing);

        Rest();
        Assert.Equal("Priority 1 — urgent", outline.TipShowing);
    }

    [WinFormsFact]
    public void A_selected_rows_dot_still_brings_the_tip_up()
    {
        // Selecting a row is a click, and a click used to put the tip away until the pointer
        // had left the whole list. Now it's away until the pointer moves again.
        using var form = Window();
        using var outline = Outline(form);

        var dot = PriorityCellOf(outline, 1);

        outline.PointerAt(dot);
        Rest();
        Assert.Equal("Priority 2 — high", outline.TipShowing);

        RealMouse.Click(outline, dot);
        Assert.Equal("b", outline.SelectedId);
        Assert.Null(outline.TipShowing);

        outline.PointerAt(new Point(dot.X + 1, dot.Y));
        Rest();
        Assert.Equal("Priority 2 — high", outline.TipShowing);
    }

    [WinFormsFact]
    public void Moving_off_the_dots_takes_the_tip_away()
    {
        using var form = Window();
        using var outline = Outline(form);

        outline.PointerAt(PriorityCellOf(outline, 0));
        Rest();
        Assert.Equal("Priority 1 — urgent", outline.TipShowing);

        outline.PointerAt(PriorityCellOf(outline, 3));

        Assert.Null(outline.TipAimedAt);
        Assert.Null(outline.TipShowing);
    }

    [WinFormsFact]
    public void Leaving_the_list_takes_the_tip_away()
    {
        using var form = Window();
        using var outline = Outline(form);

        outline.PointerAt(PriorityCellOf(outline, 0));
        Rest();
        Assert.Equal("Priority 1 — urgent", outline.TipShowing);

        PostMessage(outline.Handle, WmMouseLeave, 0, 0);
        Application.DoEvents();

        Assert.Null(outline.TipShowing);
    }

    [WinFormsFact]
    public void The_wheel_takes_the_tip_away()
    {
        // The rows move under a pointer that stays put, so the dot the tip was about has gone.
        using var form = Window();
        using var outline = Outline(form);

        outline.PointerAt(PriorityCellOf(outline, 0));
        Rest();
        Assert.Equal("Priority 1 — urgent", outline.TipShowing);

        var screen = outline.PointToScreen(PriorityCellOf(outline, 0));
        PostMessage(outline.Handle, WmMouseWheel, -120 << 16, Position(screen));
        Application.DoEvents();

        Assert.Null(outline.TipShowing);
    }
}
