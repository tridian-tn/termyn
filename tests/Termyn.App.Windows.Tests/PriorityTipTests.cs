using System.Diagnostics;
using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The tip that says what a row's priority dot means.
/// </summary>
/// <remarks>
/// The pointer's put on a dot through the tip rather than by a posted move: Windows answers a move
/// to a window the real pointer isn't over by saying it's left again, which takes the tip straight
/// back down. Presses, leaving, keys and the wheel arrive as the messages Windows sends. The timer's
/// run out by hand except where how long it takes is the point. Whether the tip window is then drawn
/// isn't asked — it's only drawn for the window in front, and a test host's can't take the front.
/// </remarks>
public class PriorityTipTests : IDisposable
{
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    private const int WmKeyDown = 0x0100;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseLeave = 0x02A3;
    private const int VkEnd = 0x23;

    private readonly string _config = Path.Combine(Path.GetTempPath(), $"termyn-tips-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_config))
            File.Delete(_config);

        GC.SuppressFinalize(this);
    }

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
    /// A list longer than the window: urgent at the top and medium the rest of the way, so what's
    /// under a still pointer changes when it scrolls.
    /// </summary>
    private static OutlineView LongOutline(Form form)
    {
        var outline = new OutlineView { Dock = DockStyle.Fill };
        form.Controls.Add(outline);
        form.Show();

        outline.Rows = Enumerable.Range(0, 80)
            .Select(i => Task($"t{i}", i < 10 ? Priority.P1 : Priority.P3))
            .ToList();

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

    /// <summary>Rests the pointer on a row's dot and runs the pause out, so the tip's up.</summary>
    private static void TipUpOn(OutlineView outline, int index)
    {
        outline.Tips.PointerAt(PriorityCellOf(outline, index));
        outline.Tips.Elapse();
    }

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

        Assert.Equal("Priority 1 — urgent", outline.Tips.Aimed);
    }

    [WinFormsFact]
    public void The_first_pause_is_half_as_long_again_as_the_toolkits()
    {
        // Waited out for real, since how long it takes is the point. Past the toolkit's half-second
        // and still down: a timer can fire late but never early, so that half can't pass by luck.
        using var form = Window();
        using var outline = Outline(form);

        outline.Tips.PointerAt(PriorityCellOf(outline, 0));
        Assert.Equal(750, outline.Tips.Pending);

        Wait(550);
        Assert.Null(outline.Tips.Showing);

        Wait(1200);
        Assert.Equal("Priority 1 — urgent", outline.Tips.Showing);
    }

    [WinFormsFact]
    public void Moving_straight_on_to_another_dot_takes_the_short_pause()
    {
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);
        outline.Tips.PointerAt(PriorityCellOf(outline, 1));

        Assert.Equal(100, outline.Tips.Pending);
    }

    [WinFormsFact]
    public void Moving_on_to_another_rows_dot_shows_that_rows_priority()
    {
        // With the whole list one tip as far as Windows was concerned, the first row's words
        // stayed up wherever the pointer went next.
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);
        Assert.Equal("Priority 1 — urgent", outline.Tips.Showing);

        TipUpOn(outline, 1);
        Assert.Equal("Priority 2 — high", outline.Tips.Showing);
    }

    [WinFormsFact]
    public void Another_rows_dot_counts_as_new_even_when_it_says_the_same()
    {
        // The tip comes down and back up by the new row, rather than staying beside the old one.
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);

        outline.Tips.PointerAt(PriorityCellOf(outline, 5));
        Assert.Null(outline.Tips.Showing);

        outline.Tips.Elapse();
        Assert.Equal("Priority 1 — urgent", outline.Tips.Showing);
    }

    [WinFormsFact]
    public void A_tip_thats_been_up_long_enough_goes_and_the_next_dot_waits_the_full_pause()
    {
        // Left to Windows to take down, the tip went with nothing here knowing, and the next dot
        // came up after the short pause meant for moving between two tips that are up.
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);
        Assert.Equal(5000, outline.Tips.Pending);

        outline.Tips.Elapse();
        Assert.Null(outline.Tips.Showing);

        outline.Tips.PointerAt(PriorityCellOf(outline, 1));
        Assert.Equal(750, outline.Tips.Pending);
    }

    [WinFormsFact]
    public void Resting_on_the_dot_a_tip_went_from_doesnt_bring_it_straight_back()
    {
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);
        outline.Tips.Elapse();

        var dot = PriorityCellOf(outline, 0);
        outline.Tips.PointerAt(new Point(dot.X + 1, dot.Y));

        Assert.Null(outline.Tips.Pending);
        Assert.Null(outline.Tips.Showing);
    }

    [WinFormsFact]
    public void A_selected_rows_dot_still_brings_the_tip_up()
    {
        // Selecting a row is a click, and a click used to put the tip away until the pointer
        // had left the whole list. Now it's away until the pointer moves again.
        using var form = Window();
        using var outline = Outline(form);

        var dot = PriorityCellOf(outline, 1);
        TipUpOn(outline, 1);

        RealMouse.Click(outline, dot);
        Assert.Equal("b", outline.SelectedId);
        Assert.Null(outline.Tips.Showing);

        outline.Tips.PointerAt(new Point(dot.X + 1, dot.Y));
        outline.Tips.Elapse();
        Assert.Equal("Priority 2 — high", outline.Tips.Showing);
    }

    [WinFormsFact]
    public void Moving_off_the_dots_takes_the_tip_away()
    {
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);
        outline.Tips.PointerAt(PriorityCellOf(outline, 3));

        Assert.Null(outline.Tips.Aimed);
        Assert.Null(outline.Tips.Showing);
    }

    [WinFormsFact]
    public void Leaving_the_list_takes_the_tip_away()
    {
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);

        PostMessage(outline.Handle, WmMouseLeave, 0, 0);
        Application.DoEvents();

        Assert.Null(outline.Tips.Showing);
    }

    [WinFormsFact]
    public void The_wheel_takes_the_tip_away()
    {
        // The rows move under a pointer that stays put, so the dot the tip was about has gone.
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);

        var screen = outline.PointToScreen(PriorityCellOf(outline, 0));
        PostMessage(outline.Handle, WmMouseWheel, -120 << 16, Position(screen));
        Application.DoEvents();

        Assert.Null(outline.Tips.Showing);
    }

    [WinFormsFact]
    public void A_key_that_scrolls_the_list_points_the_tip_at_whats_under_the_pointer_now()
    {
        // End takes the list to its last rows without the pointer moving, so the urgent task the
        // tip was about has gone from under it and a medium one has taken its place.
        using var form = Window();
        using var outline = LongOutline(form);

        TipUpOn(outline, 2);
        Assert.Equal("Priority 1 — urgent", outline.Tips.Showing);

        SendMessage(outline.Handle, WmKeyDown, VkEnd, 0);
        Application.DoEvents();

        Assert.Null(outline.Tips.Showing);
        Assert.Equal("Priority 3 — medium", outline.Tips.Aimed);
    }

    [WinFormsFact]
    public void Bringing_a_task_into_view_points_the_tip_at_whats_under_the_pointer_now()
    {
        // A search or the palette picks a task far down, and the list scrolls to it under a
        // pointer that hasn't moved.
        using var form = Window();
        using var outline = LongOutline(form);

        TipUpOn(outline, 2);
        outline.SelectId("t79");

        Assert.Equal("Priority 3 — medium", outline.Tips.Aimed);
    }

    [WinFormsFact]
    public void A_sync_that_changes_the_priority_under_the_pointer_changes_the_tip()
    {
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);
        outline.Rows = [Task("a", Priority.P2), .. outline.Rows.Skip(1)];

        Assert.Equal("Priority 2 — high", outline.Tips.Aimed);
    }

    [WinFormsFact]
    public void A_sync_after_the_pointer_has_left_brings_no_tip()
    {
        // Where the pointer was last seen is somewhere it no longer is, so there's nothing to ask
        // again about.
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);
        PostMessage(outline.Handle, WmMouseLeave, 0, 0);
        Application.DoEvents();

        outline.Rows = [Task("a", Priority.P2), .. outline.Rows.Skip(1)];

        Assert.Null(outline.Tips.Aimed);
        Assert.Null(outline.Tips.Pending);
    }

    [WinFormsFact]
    public void Switched_off_no_dot_brings_the_tip_up()
    {
        using var form = Window();
        using var outline = Outline(form);

        outline.PriorityTips = false;
        outline.Tips.PointerAt(PriorityCellOf(outline, 0));

        Assert.Null(outline.Tips.Aimed);
        Assert.Null(outline.Tips.Pending);
    }

    [WinFormsFact]
    public void Switching_it_off_takes_down_a_tip_thats_up()
    {
        using var form = Window();
        using var outline = Outline(form);

        TipUpOn(outline, 0);
        outline.PriorityTips = false;

        Assert.Null(outline.Tips.Showing);
    }

    [WinFormsFact]
    public void An_access_key_marker_in_the_words_isnt_shown()
    {
        // The words are the Priority menu's, and a menu label can carry an & for its access key.
        using var form = Window();
        using var outline = Outline(form);

        Assert.True(outline.Tips.StripsAmpersands);
    }

    [WinFormsTheory]
    [InlineData(32, true, 0, 32)]     // a colour arrow, hotspot at its tip
    [InlineData(64, false, 0, 32)]    // monochrome: the mask is two images stacked
    [InlineData(64, true, 4, 60)]     // an enlarged pointer, the size setting turned up
    [InlineData(32, true, 40, 0)]     // a hotspot below the image, which can't put the tip above it
    public void The_tip_clears_the_pointer_as_its_drawn(int maskHeight, bool hasColour, int hotspotY, int expected)
        => Assert.Equal(expected, HoverTip.DropOf(maskHeight, hasColour, hotspotY));

    [WinFormsFact]
    public void The_pointer_on_screen_can_be_measured()
    {
        // Whatever the machine's pointer is, it reaches some way below its hotspot.
        Assert.True(HoverTip.CursorDrop() > 0);
    }

    [WinFormsFact]
    public void A_window_opened_with_tips_switched_off_has_none()
    {
        File.WriteAllText(_config, """{ "schemaVersion": 1, "showPriorityTips": false }""");

        using var window = TestWindow.Start(_config, new InMemorySnapshotStore(), new FakeApi(), out _);

        Assert.False(TestWindow.Find<OutlineView>(window).PriorityTips);
    }

    [WinFormsFact]
    public void Saving_the_setting_switches_the_tips_straight_away()
    {
        using var window = TestWindow.Start(_config, new InMemorySnapshotStore(), new FakeApi(), out _);
        var outline = TestWindow.Find<OutlineView>(window);
        Assert.True(outline.PriorityTips);

        window.ApplySettings(new AppSettings { ShowPriorityTips = false });
        Assert.False(outline.PriorityTips);

        window.ApplySettings(new AppSettings { ShowPriorityTips = true });
        Assert.True(outline.PriorityTips);
    }
}
