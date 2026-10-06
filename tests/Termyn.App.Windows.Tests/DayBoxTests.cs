using Termyn.Core.Capture;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The box a day is asked for in, by the due date and the deadline alike: typed, or picked off the
/// calendar beside it, with a line under it saying what it was read as.
/// </summary>
public class DayBoxTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [WinFormsFact]
    public void Typing_a_day_says_which_day_it_was_read_as()
    {
        using var box = Box();

        box.Typed = "4 aug";

        Assert.Equal(DayReading.On(new DateOnly(2026, 8, 4)), box.Reading);
        Assert.Equal(DayBoxText.ForDue(box.Reading).Says, box.Says);
    }

    [WinFormsFact]
    public void Each_change_to_the_box_is_announced()
    {
        // The dialogs hang their OK button and their time field off this, so a reading that changed
        // without saying so would leave both showing the last one.
        using var box = Box();
        var told = 0;
        box.ReadingChanged += (_, _) => told++;

        box.Typed = "tomorrow";
        box.Typed = "tomorrow 4pm";

        Assert.Equal(2, told);
    }

    [WinFormsFact]
    public void Picking_a_day_writes_it_in_words_the_box_reads_back()
    {
        using var box = Box();

        box.PickFromCalendar(new DateOnly(2026, 8, 11));

        Assert.Equal("11 Aug 2026", box.Typed);
        Assert.Equal(DayReading.On(new DateOnly(2026, 8, 11)), box.Reading);
    }

    [WinFormsFact]
    public void Picking_a_day_keeps_the_time_already_typed()
    {
        // Picking another Tuesday isn't asking to lose the nine o'clock.
        using var box = Box();
        box.Typed = "tomorrow 9am";

        box.PickFromCalendar(new DateOnly(2026, 8, 11));

        Assert.Equal("11 Aug 2026 09:00", box.Typed);
    }

    [WinFormsFact]
    public void Picking_a_day_over_a_repeat_replaces_it()
    {
        // The calendar is for one-off days, and a repeat has none of its own to keep.
        using var box = Box();
        box.Typed = "every monday";

        box.PickFromCalendar(new DateOnly(2026, 8, 11));

        Assert.Equal("11 Aug 2026", box.Typed);
    }

    [WinFormsFact]
    public void A_time_set_on_a_day_is_written_into_the_box()
    {
        using var box = Box();
        box.Typed = "tomorrow";

        box.SetTime(new TimeOnly(16, 30));

        Assert.Equal("1 Aug 2026 16:30", box.Typed);
    }

    [WinFormsFact]
    public void A_time_has_nothing_to_go_on_while_the_box_isnt_a_day()
    {
        using var box = Box();
        box.Typed = "every monday";

        box.SetTime(new TimeOnly(16, 30));

        Assert.Equal("every monday", box.Typed);
    }

    [WinFormsFact]
    public void The_calendar_opens_on_the_day_the_box_reads_as_without_rewriting_it()
    {
        // Moving the calendar there isn't a choice of that day. Written back, "tomorrow" would turn
        // into a date just for having been looked at.
        using var box = Shown(Box());
        box.Typed = "tomorrow";

        box.PressPick();

        Assert.Equal(new DateOnly(2026, 8, 1), box.CalendarDay);
        Assert.Equal("tomorrow", box.Typed);
    }

    [WinFormsFact]
    public void The_calendar_opens_on_today_when_the_box_names_no_day()
    {
        using var box = Shown(Box());

        box.PressPick();

        Assert.Equal(Today, box.CalendarDay);
    }

    [WinFormsFact]
    public void A_day_the_calendar_cant_show_opens_it_on_the_earliest_it_can()
    {
        // The control throws outright on a date before 1753, which would leave a task carrying one
        // from an import unreachable from here for good.
        using var box = Shown(Box());
        box.Typed = "1 May 1600";

        box.PressPick();

        Assert.Equal(new DateOnly(1753, 1, 1), box.CalendarDay);
        Assert.Equal("1 May 1600", box.Typed);
    }

    [WinFormsFact]
    public void The_calendar_takes_the_keys_when_it_opens()
    {
        // The whole keyboard route depends on this: a control inside a drop-down isn't on the
        // form's own focus chain, so asking the calendar itself to take the focus does nothing and
        // the calendar sits open and deaf.
        using var box = Shown(Box());

        box.PressPick();

        Assert.True(box.CalendarOpen);
        Assert.True(box.CalendarFocused);
    }

    [WinFormsFact]
    public void Enter_and_escape_close_the_calendar_without_reaching_the_dialog()
    {
        // The dialog answers Escape with Cancel and Enter with OK. A key let through from the
        // calendar would shut both in one press, throwing away the day just settled on.
        using var box = Box();

        foreach (var key in new[] { Keys.Enter, Keys.Escape })
        {
            var pressed = box.PressInCalendar(key);

            Assert.True(pressed.Handled);
            Assert.True(pressed.SuppressKeyPress);
        }

        // And anything else is left alone, or the calendar couldn't be typed into at all.
        Assert.False(box.PressInCalendar(Keys.Down).Handled);
    }

    [WinFormsFact]
    public void The_button_that_opens_the_calendar_says_what_it_is()
    {
        // It carries a drawn calendar and no words, so hovering has to answer what it does.
        using var box = Box();

        Assert.Equal("Pick a day", box.PickTip);
        Assert.Equal(new Size(box.LogicalToDeviceUnits(16), box.LogicalToDeviceUnits(16)), box.GlyphDrawn);
    }

    [WinFormsFact]
    public void What_the_box_built_is_let_go_of_when_it_is()
    {
        // Neither the calendar's drop nor the button's image is a child control, so the base
        // disposal doesn't reach them.
        var box = Box();

        box.Dispose();

        Assert.True(box.Released);
    }

    /// <summary>A box judged as the due date's is, reading with the grammar quick add uses.</summary>
    /// <returns>The box, which the caller disposes</returns>
    private static DayBox Box()
        => new(
            text => new QuickAddParser(new FixedClock(Today)).ReadDay(text, new DateSettings(TimeZoneInfo.Utc)),
            DayBoxText.ForDue,
            Today,
            takesTime: true);

    /// <summary>
    /// Puts a box on a window shown off-screen, since one nobody has displayed has no handle to
    /// focus and no place to hang a drop-down off.
    /// </summary>
    /// <param name="box">The box</param>
    /// <returns>The same box, now on a shown window that goes when the box does</returns>
    private static DayBox Shown(DayBox box)
    {
        var window = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        window.Controls.Add(box);
        box.Disposed += (_, _) => window.Dispose();
        window.Show();
        return box;
    }
}
