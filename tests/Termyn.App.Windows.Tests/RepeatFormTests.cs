using Termyn.Core.Capture;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The dialog behind Custom…, which builds a repeat in parts. Built rather than shown, like the
/// due-date dialog: what it opens on and the words it comes back with are what matter.
/// </summary>
public class RepeatFormTests
{
    /// <summary>A Friday.</summary>
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>A Tuesday.</summary>
    private static readonly DateOnly Due = new(2026, 8, 4);

    private static RepeatForm Build(RepeatRule? rule = null, TimeOnly? time = null, string? unreadable = null)
        => RepeatForm.For(rule, Due, time, unreadable, Today);

    [WinFormsFact]
    public void A_new_repeat_starts_weekly_on_the_tasks_own_day_and_time()
    {
        using var dialog = Build(time: new TimeOnly(9, 30));

        Assert.Equal("every tue at 09:30 starting 2026-08-04", dialog.Words);
        Assert.Equal("Todoist will read this as \"every tue at 09:30 starting 2026-08-04\".", dialog.Preview);
        Assert.Equal(string.Empty, dialog.Unreadable);
    }

    [WinFormsFact]
    public void It_opens_on_the_repeat_it_was_given()
    {
        var rule = RepeatWords.Read("every 3 months at 08:00 until 1 Jun 2027", Today)!;
        using var dialog = Build(rule);

        Assert.Equal("every 3 months at 08:00 starting 2026-08-04 until 2027-06-01", dialog.Words);
        Assert.Equal(3, dialog.Interval.Value);
        Assert.Equal("months", dialog.Unit.Text);
    }

    [WinFormsFact]
    public void Days_of_the_week_are_ticked_for_a_week_at_a_time()
    {
        using var dialog = Build();

        dialog.Tick(DayOfWeek.Friday, true);

        Assert.True(dialog.DaysOffered);
        Assert.Equal("every tue, fri starting 2026-08-04", dialog.Words);
    }

    [WinFormsFact]
    public void Days_of_the_week_go_where_todoist_has_no_words_for_them()
    {
        // "every 2 weeks on tue" isn't in Todoist's help, so a fortnight falls on the day it starts.
        using var dialog = Build();

        dialog.Interval.Value = 2;

        Assert.False(dialog.DaysOffered);
        Assert.Equal("every 2 weeks starting 2026-08-04", dialog.Words);
        Assert.Equal("weeks", dialog.Unit.Text);
    }

    [WinFormsFact]
    public void Counted_from_completion_is_every_with_a_mark()
    {
        using var dialog = Build();

        dialog.Unit.SelectedIndex = (int)RepeatUnit.Day;
        dialog.Interval.Value = 10;
        dialog.CountFromCompletion();

        Assert.False(dialog.DaysOffered);
        Assert.StartsWith("every! 10 days", dialog.Words);
    }

    [WinFormsFact]
    public void A_month_falls_on_the_day_it_starts()
    {
        using var dialog = Build();

        dialog.Unit.SelectedIndex = (int)RepeatUnit.Month;

        Assert.Equal("every 4th starting 2026-08-04", dialog.Words);
    }

    [WinFormsFact]
    public void An_end_date_is_added_and_taken_away()
    {
        using var dialog = Build();

        dialog.EndOn(new DateOnly(2026, 12, 31));
        Assert.Equal("every tue starting 2026-08-04 until 2026-12-31", dialog.Words);

        dialog.EndOn(null);
        Assert.Equal("every tue starting 2026-08-04", dialog.Words);
    }

    [WinFormsFact]
    public void A_repeat_it_cannot_take_apart_is_named_rather_than_shown()
    {
        using var _ = new CultureScope("en-GB");
        using var dialog = Build(unreadable: "every other fri");

        Assert.Equal("This task repeats \"every other fri\", which can't be shown here. Saving replaces it.", dialog.Unreadable);
        Assert.Equal("every tue starting 2026-08-04", dialog.Words);
    }
}
