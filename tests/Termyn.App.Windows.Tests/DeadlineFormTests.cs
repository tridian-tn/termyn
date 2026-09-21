using System.Globalization;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The dialog that asks for the day a task has to be finished by.
/// </summary>
/// <remarks>
/// Built rather than shown: what the dialog is asked for, and what it answers, are the two things
/// the window's side of this gets wrong. Showing it would need somebody to click.
/// </remarks>
public class DeadlineFormTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [WinFormsFact]
    public void It_opens_on_the_deadline_the_task_already_has()
    {
        using var dialog = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);

        Assert.Equal(new DateOnly(2026, 8, 4), dialog.Chosen);
    }

    [WinFormsFact]
    public void A_task_with_no_deadline_opens_on_today()
    {
        // The day somebody reaching for this is most often counting from, and never an empty box
        // the picker would have to invent a value for anyway.
        using var dialog = DeadlineForm.For("Ship it", null, Today);

        Assert.Equal(Today, dialog.Chosen);
    }

    [WinFormsFact]
    public void The_day_is_written_out_on_the_face_of_the_dialog()
    {
        // The whole of what the dialog says the answer is: there's no date control to read it off,
        // so a day that didn't reach this label would be a day nobody could see they were setting.
        InBritish(() =>
        {
            using var dialog = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);

            Assert.Equal("Tuesday, 4 August 2026", dialog.DayShown);
        });
    }

    [WinFormsFact]
    public void Picking_a_day_from_the_calendar_shows_it_and_answers_with_it()
    {
        InBritish(() =>
        {
            using var dialog = DeadlineForm.For("Ship it", null, Today);

            dialog.PickFromCalendar(new DateOnly(2026, 8, 11));

            Assert.Equal(new DateOnly(2026, 8, 11), dialog.Chosen);
            Assert.Equal("Tuesday, 11 August 2026", dialog.DayShown);
        });
    }

    [WinFormsFact]
    public void A_deadline_the_calendar_cant_show_opens_on_the_earliest_it_can()
    {
        // A deadline is whatever the account's JSON said, and the control throws outright on a date
        // before 1753 — which would leave that task's deadline unreachable from here for good.
        using var dialog = DeadlineForm.For("Ship it", new DateOnly(1600, 5, 1), Today);

        Assert.Equal(new DateOnly(1753, 1, 1), dialog.Chosen);
    }

    [WinFormsFact]
    public void Enter_and_escape_close_the_calendar_without_reaching_the_dialog()
    {
        // The dialog answers Escape with Cancel and Enter with OK. A key let through from the
        // calendar would shut both in one press, throwing away the day just settled on.
        using var dialog = DeadlineForm.For("Ship it", null, Today);

        foreach (var key in new[] { Keys.Enter, Keys.Escape })
        {
            var pressed = dialog.PressInCalendar(key);

            Assert.True(pressed.Handled);
            Assert.True(pressed.SuppressKeyPress);
        }

        // And anything else is left alone, or the calendar couldn't be typed into at all.
        Assert.False(dialog.PressInCalendar(Keys.Down).Handled);
    }

    [WinFormsFact]
    public void The_button_that_opens_the_calendar_says_what_it_is()
    {
        // It carries a drawn calendar and no words, and the day beside it is a label rather than
        // anything that looks pressable — so hovering has to answer what it does.
        using var dialog = DeadlineForm.For("Ship it", null, Today);

        Assert.Equal("Pick a day", dialog.PickTip);
        Assert.Equal(new Size(dialog.LogicalToDeviceUnits(16), dialog.LogicalToDeviceUnits(16)), dialog.GlyphDrawn);
    }

    [WinFormsFact]
    public void What_the_dialog_built_is_let_go_of_when_it_is()
    {
        // Neither the calendar's drop nor the button's image is a child control, so the base
        // disposal doesn't reach them — and a dialog built and dropped without being shown raises
        // no close to hang them off, which is every test in this file.
        var dialog = DeadlineForm.For("Ship it", null, Today);

        dialog.Dispose();

        Assert.True(dialog.Released);
    }

    [WinFormsFact]
    public void Clearing_answers_with_no_day_at_all()
    {
        using var dialog = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);

        dialog.PressClear();

        Assert.Null(dialog.Chosen);
    }

    [WinFormsFact]
    public void There_is_nothing_to_clear_on_a_task_that_hasnt_got_one()
    {
        using var withOne = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);
        using var without = DeadlineForm.For("Ship it", null, Today);

        Assert.True(withOne.CanClear);
        Assert.False(without.CanClear);
    }

    [WinFormsFact]
    public void Clearing_closes_the_dialog_as_an_answer_rather_than_a_cancel()
    {
        // Clear is a change like picking a day is, so it leaves by the same door: a cancel would
        // send the caller away thinking nothing had been asked for.
        using var dialog = DeadlineForm.For("Ship it", new DateOnly(2026, 8, 4), Today);

        dialog.PressClear();

        Assert.Equal(DialogResult.OK, dialog.DialogResult);
    }

    [WinFormsFact]
    public void The_calendar_takes_the_keys_when_it_opens()
    {
        // The whole keyboard route depends on this: a control inside a drop-down isn't on the
        // form's own focus chain, so asking the calendar itself to take the focus does nothing and
        // the calendar sits open and deaf. Shown off-screen, since a window nobody has displayed
        // has no handle to focus and no place to hang a drop-down off.
        using var dialog = DeadlineForm.For("Ship it", null, Today);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-2000, -2000);
        dialog.Show();

        dialog.PressPick();

        Assert.True(dialog.CalendarOpen);
        Assert.True(dialog.CalendarFocused);
    }

    [WinFormsFact]
    public void The_task_is_named_with_its_own_punctuation()
    {
        // An ampersand in a task's name is a character, not the mark of an accelerator: left on,
        // "Books & Papers" reads "Books Papers" with the P underlined.
        using var dialog = DeadlineForm.For("Books & Papers", null, Today);

        var naming = dialog.Controls.OfType<Label>().Single(l => l.Text == "Books & Papers");

        Assert.False(naming.UseMnemonic);
    }

    [WinFormsFact]
    public void The_task_is_named_above_the_question_about_it()
    {
        // What's being changed, then what's being asked about it — the other way round, the dialog
        // opens with a question and only afterwards says what it is about.
        using var dialog = DeadlineForm.For("Ship it", null, Today);

        var labels = dialog.Controls.OfType<Label>().OrderBy(l => l.Top).Select(l => l.Text).ToArray();

        Assert.Equal(["Ship it", "Finish it by:"], labels[..2]);
    }

    /// <summary>
    /// Runs a test with the machine set to en-GB.
    /// </summary>
    /// <remarks>
    /// The day is written in the user's own culture, as a date shown to somebody should be — which
    /// leaves the wording unassertable unless the culture is said. Set around the whole body, since
    /// the dialog writes the day both when it's built and each time one is picked.
    /// </remarks>
    /// <param name="body">The test</param>
    private static void InBritish(Action body)
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-GB");
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}
