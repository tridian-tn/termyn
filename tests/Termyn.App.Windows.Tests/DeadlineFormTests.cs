using System.Globalization;
using Termyn.Core.Capture;
using Termyn.Presentation;
using Termyn.TestSupport;

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
        using var dialog = Deadline(new DateOnly(2026, 8, 4));

        Assert.Equal("4 Aug 2026", dialog.Box.Typed);
        Assert.Equal(new DateOnly(2026, 8, 4), dialog.Chosen);
    }

    [WinFormsFact]
    public void It_opens_in_figures_for_an_account_that_reads_another_language()
    {
        // The box reads only figures for it, so "4 Aug 2026" would open on a deadline it refused.
        using var dialog = Deadline(new DateOnly(2026, 8, 4), "de");

        Assert.Equal("2026-08-04", dialog.Box.Typed);
        Assert.Equal(new DateOnly(2026, 8, 4), dialog.Chosen);
        Assert.True(dialog.CanAccept);
    }

    [WinFormsFact]
    public void Words_are_refused_with_figures_offered_for_an_account_that_reads_another_language()
    {
        using var dialog = Deadline(null, "de");

        dialog.Box.Typed = "4 aug";

        Assert.False(dialog.CanAccept);
        Assert.Equal(DayBoxText.ForDeadline(DayReading.Unread, english: false).Says, dialog.Box.Says);
    }

    [WinFormsFact]
    public void A_task_with_no_deadline_opens_on_an_empty_box()
    {
        // The same as a due date, which is the point of the two sharing a box. OK on it unchanged
        // answers with no deadline, which on a task that hasn't got one is no change at all.
        using var dialog = Deadline(null);

        Assert.Equal(string.Empty, dialog.Box.Typed);
        Assert.Null(dialog.Chosen);
        Assert.True(dialog.CanAccept);
    }

    [WinFormsFact]
    public void The_day_is_written_out_under_the_box()
    {
        InBritish(() =>
        {
            using var dialog = Deadline(new DateOnly(2026, 8, 4));

            Assert.Equal("Tuesday, 4 August 2026", dialog.Box.Says);
        });
    }

    [WinFormsFact]
    public void A_day_typed_is_the_day_it_answers_with()
    {
        using var dialog = Deadline(null);

        dialog.Box.Typed = "next month";

        Assert.Equal(new DateOnly(2026, 8, 31), dialog.Chosen);
        Assert.True(dialog.CanAccept);
    }

    [WinFormsFact]
    public void Picking_a_day_from_the_calendar_writes_it_into_the_box()
    {
        using var dialog = Deadline(null);

        dialog.Box.PickFromCalendar(new DateOnly(2026, 8, 11));

        Assert.Equal("11 Aug 2026", dialog.Box.Typed);
        Assert.Equal(new DateOnly(2026, 8, 11), dialog.Chosen);
    }

    [WinFormsTheory]
    [InlineData("every monday")]
    [InlineData("tomorrow 4pm")]
    [InlineData("in three weeks")]
    public void What_a_deadline_cannot_be_is_refused(string typed)
    {
        // A deadline is a date and nothing else, and there's no server to hand words to: the API has
        // no field to read them into. So OK waits for something it can send.
        using var dialog = Deadline(null);

        dialog.Box.Typed = typed;

        Assert.False(dialog.CanAccept);
    }

    [WinFormsFact]
    public void Refusing_says_why_under_the_box()
    {
        using var dialog = Deadline(null);

        dialog.Box.Typed = "every monday";

        Assert.Equal("A deadline can't repeat", dialog.Box.Says);
    }

    [WinFormsFact]
    public void Picking_a_day_over_a_time_drops_the_time()
    {
        // A deadline can't hold one, so keeping it would leave the box refusing the day just picked.
        using var dialog = Deadline(null);
        dialog.Box.Typed = "tomorrow 4pm";

        dialog.Box.PickFromCalendar(new DateOnly(2026, 8, 11));

        Assert.Equal("11 Aug 2026", dialog.Box.Typed);
        Assert.True(dialog.CanAccept);
    }

    [WinFormsFact]
    public void A_deadline_the_calendar_cant_show_still_answers_with_itself()
    {
        // A deadline is whatever the account's JSON said, and the calendar throws on anything before
        // 1753. The box holds the real day, so closing the dialog unchanged doesn't move it to the
        // earliest one the calendar could show.
        using var dialog = Deadline(new DateOnly(1600, 5, 1));

        Assert.Equal(new DateOnly(1600, 5, 1), dialog.Chosen);
    }

    [WinFormsFact]
    public void Clearing_answers_with_no_day_at_all()
    {
        using var dialog = Deadline(new DateOnly(2026, 8, 4));

        dialog.PressClear();

        Assert.Null(dialog.Chosen);
    }

    [WinFormsFact]
    public void There_is_nothing_to_clear_on_a_task_that_hasnt_got_one()
    {
        using var withOne = Deadline(new DateOnly(2026, 8, 4));
        using var without = Deadline(null);

        Assert.True(withOne.CanClear);
        Assert.False(without.CanClear);
    }

    [WinFormsFact]
    public void Clearing_closes_the_dialog_as_an_answer_rather_than_a_cancel()
    {
        // Clear is a change like picking a day is, so it leaves by the same door: a cancel would
        // send the caller away thinking nothing had been asked for.
        using var dialog = Deadline(new DateOnly(2026, 8, 4));

        dialog.PressClear();

        Assert.Equal(DialogResult.OK, dialog.DialogResult);
    }

    [WinFormsFact]
    public void The_task_is_named_with_its_own_punctuation()
    {
        // An ampersand in a task's name is a character, not the mark of an accelerator: left on,
        // "Books & Papers" reads "Books Papers" with the P underlined.
        using var dialog = DeadlineForm.For("Books & Papers", null, Today, Read, english: true);

        var naming = dialog.Controls.OfType<Label>().Single(l => l.Text == "Books & Papers");

        Assert.False(naming.UseMnemonic);
    }

    [WinFormsFact]
    public void The_task_is_named_above_the_question_about_it()
    {
        // What's being changed, then what's being asked about it — the other way round, the dialog
        // opens with a question and only afterwards says what it is about.
        using var dialog = Deadline(null);

        var labels = dialog.Controls.OfType<Label>().OrderBy(l => l.Top).Select(l => l.Text).ToArray();

        Assert.Equal(["Ship it", "Finish it by:"], labels[..2]);
    }

    /// <summary>The dialog for a task called "Ship it", read with the grammar quick add uses.</summary>
    /// <param name="current">The deadline it has now, or null</param>
    /// <param name="language">The language the account reads its dates in, or null when it isn't known</param>
    /// <returns>The dialog, which the caller disposes</returns>
    private static DeadlineForm Deadline(DateOnly? current, string? language = null)
    {
        var settings = new DateSettings(TimeZoneInfo.Utc, Language: language);

        return DeadlineForm.For(
            "Ship it",
            current,
            Today,
            text => new QuickAddParser(new FixedClock(Today)).ReadDay(text, settings),
            settings.ReadsEnglish);
    }

    /// <summary>Reads a box's text as the app does, on a day fixed for the test.</summary>
    private static DayReading Read(string text)
        => new QuickAddParser(new FixedClock(Today)).ReadDay(text, new DateSettings(TimeZoneInfo.Utc));

    /// <summary>
    /// Runs a test with the machine set to en-GB.
    /// </summary>
    /// <remarks>
    /// The day is written in the user's own culture, as a date shown to somebody should be — which
    /// leaves the wording unassertable unless the culture is said. Set around the whole body, since
    /// the box writes the day both when it's built and each time it's read again.
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
