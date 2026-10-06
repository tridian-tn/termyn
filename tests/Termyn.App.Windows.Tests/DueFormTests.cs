using System.Globalization;
using Termyn.Core.Capture;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The dialog that asks when a task is due: the day box, and a time field beside it.
/// </summary>
/// <remarks>
/// Built rather than shown, like the deadline's: what it opens on and what it answers with are the
/// things to get right, and neither needs anybody to click.
/// </remarks>
public class DueFormTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [WinFormsFact]
    public void It_opens_on_the_due_date_it_was_given()
    {
        using var dialog = Due("1 Aug 2026 16:00");

        Assert.Equal("1 Aug 2026 16:00", dialog.Box.Typed);
        Assert.Equal("1 Aug 2026 16:00", dialog.Answer);
    }

    [WinFormsFact]
    public void The_time_field_shows_the_time_the_box_reads_as()
    {
        using var dialog = Due("1 Aug 2026 16:00");

        Assert.True(dialog.TimeOffered);
        Assert.Equal(new TimeOnly(16, 0), dialog.TimeShown);
    }

    [WinFormsFact]
    public void A_whole_day_leaves_the_time_field_unticked()
    {
        using var dialog = Due("1 Aug 2026");

        Assert.True(dialog.TimeOffered);
        Assert.Null(dialog.TimeShown);
    }

    [WinFormsFact]
    public void An_unticked_field_shows_the_morning_rather_than_when_it_was_made()
    {
        // The native field only shows a time it's been sent, and one built unticked never is: it
        // showed the minute the dialog opened, greyed, and ticking it set the task to that. Read off
        // the native field itself, since the control's own value said nine o'clock all along.
        using var dialog = Due("tomorrow");
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-2000, -2000);
        dialog.Show();

        var field = dialog.Controls.OfType<DateTimePicker>().Single();

        Assert.False(field.Checked);
        Assert.Equal(new DateTime(2000, 1, 1, 9, 0, 0).ToString(field.CustomFormat, CultureInfo.CurrentCulture), field.Text);
    }

    [WinFormsFact]
    public void Typing_a_time_into_the_box_moves_the_field()
    {
        using var dialog = Due(string.Empty);

        dialog.Box.Typed = "tomorrow at 4:30pm";

        Assert.Equal(new TimeOnly(16, 30), dialog.TimeShown);
    }

    [WinFormsFact]
    public void Setting_a_time_in_the_field_writes_it_into_the_box()
    {
        // The box is the whole answer, so the field doesn't keep a time of its own beside it.
        using var dialog = Due("tomorrow");

        dialog.ChooseTime(new TimeOnly(9, 15));

        Assert.Equal("1 Aug 2026 09:15", dialog.Answer);
    }

    [WinFormsFact]
    public void Unticking_the_field_takes_the_time_out_of_the_box()
    {
        using var dialog = Due("1 Aug 2026 16:00");

        dialog.ChooseTime(null);

        Assert.Equal("1 Aug 2026", dialog.Answer);
    }

    [WinFormsTheory]
    [InlineData("every monday")]
    [InlineData("in three weeks")]
    [InlineData("")]
    public void The_time_field_is_greyed_while_the_box_isnt_a_day(string typed)
    {
        // A repeat carries its time in its own words, and words the grammar here doesn't read are
        // the server's to settle — there's no day here to put a time on.
        using var dialog = Due(typed);

        Assert.False(dialog.TimeOffered);
    }

    [WinFormsFact]
    public void Words_this_cant_read_are_still_an_answer()
    {
        // Unlike a deadline: a due date the grammar here doesn't read goes to Todoist as the words,
        // which is how a repeat gets set at all.
        using var dialog = Due(string.Empty);

        dialog.Box.Typed = "every other tuesday";

        Assert.Equal("every other tuesday", dialog.Answer);
    }

    [WinFormsFact]
    public void Clearing_answers_with_nothing_and_closes_as_an_answer()
    {
        using var dialog = Due("1 Aug 2026");

        dialog.PressClear();

        Assert.Equal(string.Empty, dialog.Answer);
        Assert.Equal(DialogResult.OK, dialog.DialogResult);
    }

    [WinFormsFact]
    public void There_is_nothing_to_clear_on_a_task_that_isnt_due()
    {
        using var due = Due("1 Aug 2026");
        using var notDue = Due(string.Empty);

        Assert.True(due.CanClear);
        Assert.False(notDue.CanClear);
    }

    [WinFormsFact]
    public void The_task_is_named_above_the_question_about_it()
    {
        using var dialog = Due(string.Empty);

        var labels = dialog.Controls.OfType<Label>().OrderBy(l => l.Top).Select(l => l.Text).ToArray();

        Assert.Equal(["Ship it", "When is it due?"], labels[..2]);
    }

    /// <summary>The dialog for a task called "Ship it", read with the grammar quick add uses.</summary>
    /// <param name="current">The due date the box opens on</param>
    /// <returns>The dialog, which the caller disposes</returns>
    private static DueForm Due(string current)
        => DueForm.For(
            "Ship it",
            current,
            Today,
            text => new QuickAddParser(new FixedClock(Today)).ReadDay(text, new DateSettings(TimeZoneInfo.Utc)));
}
