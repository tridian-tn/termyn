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
        TestWindow.ShowOffScreen(dialog);

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

    // ---- Repeat --------------------------------------------------------------------------------------

    [WinFormsFact]
    public void The_repeats_offered_are_built_on_the_day_in_the_box()
    {
        // 1 Aug 2026 is a Saturday.
        using var _ = new CultureScope("en-GB");
        using var dialog = Due("1 Aug 2026");

        Assert.Equal(
            ["Doesn't repeat", "Every day", "Every week on Saturday", "Every weekday (Mon–Fri)", "Every month on day 1", "Every year on 1 Aug", "Custom…"],
            dialog.Repeat.Items.Cast<string>().ToArray());
        Assert.Equal(0, dialog.Repeat.SelectedIndex);
    }

    [WinFormsFact]
    public void Picking_a_repeat_writes_its_words_into_the_box_with_the_time()
    {
        using var dialog = Due("1 Aug 2026 16:00");

        dialog.Repeat.SelectedIndex = 2;

        Assert.Equal("every sat at 16:00 starting 2026-08-01", dialog.Answer);
        Assert.Equal(2, dialog.Repeat.SelectedIndex);
        Assert.False(dialog.TimeOffered);
    }

    [WinFormsFact]
    public void A_repeat_off_today_starts_on_the_day_in_the_box()
    {
        using var dialog = Due("4 Aug 2026");

        dialog.Repeat.SelectedIndex = 1;

        Assert.Equal("every day starting 2026-08-04", dialog.Answer);
    }

    [WinFormsFact]
    public void A_repeat_from_todoist_shows_as_the_entry_that_writes_it()
    {
        // Spelt the way Todoist's own scheduler spells it rather than the way the list writes it.
        using var dialog = Due("every Saturday", due: new DateOnly(2026, 8, 1));

        Assert.Equal("Every week on Saturday", dialog.Repeat.Text);
        Assert.Equal("every Saturday", dialog.Answer);
    }

    [WinFormsFact]
    public void A_repeat_todoist_kept_without_its_start_still_shows_as_its_entry()
    {
        // Sent as "every day starting 2026-08-04", Todoist keeps "every day" and puts the start in
        // the due date, which is where it's read back from.
        using var dialog = Due("every day", due: new DateOnly(2026, 8, 4));

        Assert.Equal("Every day", dialog.Repeat.Text);
    }

    [WinFormsFact]
    public void A_repeat_picked_on_an_overdue_task_starts_from_today()
    {
        // Built on the day the task was due, but not started there: it would be overdue the moment
        // it was set.
        using var dialog = Due("1 Jul 2026", due: new DateOnly(2026, 7, 1));

        dialog.Repeat.SelectedIndex = 1;

        Assert.Equal("every day", dialog.Answer);
    }

    [WinFormsFact]
    public void Doesnt_repeat_puts_back_the_day_a_task_was_moved_to_by_hand()
    {
        // Due on the 3rd though it repeats on the 24th. The 24th the words read as is theirs, not
        // the day the task is on.
        using var dialog = Due("every 24th", due: new DateOnly(2026, 8, 3));

        dialog.Repeat.SelectedIndex = 0;

        Assert.Equal("3 Aug 2026", dialog.Answer);
    }

    [WinFormsFact]
    public void Picking_an_entry_over_the_boxs_own_words_leaves_the_list_whole()
    {
        using var dialog = Due("every other fri", due: new DateOnly(2026, 8, 7));

        dialog.Repeat.SelectedIndex = 1;

        // The entry for the old words has gone, and Custom… is still last.
        Assert.Equal("Every day", dialog.Repeat.Text);
        Assert.Equal(7, dialog.Repeat.Items.Count);
        Assert.Equal("Custom…", dialog.Repeat.Items[^1]);
    }

    [WinFormsFact]
    public void A_repeat_the_list_has_no_entry_for_shows_as_its_own_words()
    {
        // With Custom… still below it, so there's something left to pick to change it.
        using var dialog = Due("every other fri", due: new DateOnly(2026, 8, 7));

        Assert.Equal("every other fri", dialog.Repeat.Text);
        Assert.Equal("Custom…", dialog.Repeat.Items[^1]);
        Assert.Equal("every other fri", dialog.Answer);
    }

    [WinFormsFact]
    public void Doesnt_repeat_puts_the_day_it_was_next_due_back_in_the_box()
    {
        using var dialog = Due("every tue at 09:30", due: new DateOnly(2026, 8, 4));

        dialog.Repeat.SelectedIndex = 0;

        Assert.Equal("4 Aug 2026 09:30", dialog.Answer);
    }

    [WinFormsFact]
    public void Custom_writes_the_repeat_it_builds_into_the_box()
    {
        using var dialog = Due("1 Aug 2026 16:00");

        (RepeatRule? Rule, DateOnly Day, TimeOnly? Time, string? Unreadable) asked = default;
        dialog.AskForRepeat = (rule, day, time, unreadable) =>
        {
            asked = (rule, day, time, unreadable);
            return new RepeatRule(RepeatUnit.Week, Weekdays: [DayOfWeek.Monday, DayOfWeek.Friday], Time: time);
        };

        dialog.Repeat.SelectedIndex = dialog.Repeat.Items.Count - 1;

        // Opened on the day and time in the box, since it held no repeat to open on.
        Assert.Equal((null, new DateOnly(2026, 8, 1), new TimeOnly(16, 0), null), asked);
        Assert.Equal("every mon, fri at 16:00", dialog.Answer);
        Assert.Equal("every mon, fri at 16:00", dialog.Repeat.Text);
    }

    [WinFormsFact]
    public void Custom_opens_on_the_repeat_in_the_box()
    {
        using var dialog = Due("every 2 weeks at 09:00", due: new DateOnly(2026, 8, 7));

        RepeatRule? opened = null;
        dialog.AskForRepeat = (rule, _, _, _) =>
        {
            opened = rule;
            return null;
        };

        dialog.Repeat.SelectedIndex = dialog.Repeat.Items.Count - 1;

        Assert.Equal("every 2 weeks at 09:00 starting 2026-08-07", RepeatWords.Write(opened!, Today));
    }

    [WinFormsFact]
    public void Custom_names_a_repeat_it_cannot_take_apart()
    {
        using var dialog = Due("every other fri", due: new DateOnly(2026, 8, 7));

        (RepeatRule? Rule, string? Unreadable) asked = default;
        dialog.AskForRepeat = (rule, _, _, words) =>
        {
            asked = (rule, words);
            return null;
        };

        dialog.Repeat.SelectedIndex = dialog.Repeat.Items.Count - 1;

        Assert.Equal((null, "every other fri"), asked);

        // Cancelled, so the box keeps its words and the list goes back to them.
        Assert.Equal("every other fri", dialog.Answer);
        Assert.Equal("every other fri", dialog.Repeat.Text);
    }

    [WinFormsFact]
    public void Cancelling_custom_puts_the_list_back_on_what_the_box_holds()
    {
        using var dialog = Due("1 Aug 2026");
        dialog.AskForRepeat = (_, _, _, _) => null;

        dialog.Repeat.SelectedIndex = dialog.Repeat.Items.Count - 1;

        Assert.Equal("1 Aug 2026", dialog.Answer);
        Assert.Equal(0, dialog.Repeat.SelectedIndex);
    }

    [WinFormsFact]
    public void The_list_is_greyed_for_an_account_that_reads_another_language()
    {
        // Its words are English, which such an account doesn't read.
        using var dialog = DueForm.For("Ship it", string.Empty, Today, text => DayReading.Blank, english: false);

        Assert.False(dialog.Repeat.Enabled);
    }

    /// <summary>The dialog for a task called "Ship it", read with the grammar quick add uses.</summary>
    /// <param name="current">The due date the box opens on</param>
    /// <param name="due">The day the task is due on now, or null when it has none</param>
    /// <returns>The dialog, which the caller disposes</returns>
    private static DueForm Due(string current, DateOnly? due = null)
        => DueForm.For(
            "Ship it",
            current,
            Today,
            text => new QuickAddParser(new FixedClock(Today)).ReadDay(text, new DateSettings(TimeZoneInfo.Utc)),
            english: true,
            due);
}
