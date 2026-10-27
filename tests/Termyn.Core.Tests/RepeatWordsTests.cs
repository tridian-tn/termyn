using Termyn.Core.Capture;

namespace Termyn.Core.Tests;

/// <summary>
/// A repeat written as Todoist's words and read back: only the forms its help on recurring dates
/// gives, written the way Todoist reads them rather than the way they look as if it should.
/// </summary>
public class RepeatWordsTests
{
    /// <summary>A Tuesday.</summary>
    private static readonly DateOnly Today = new(2026, 10, 27);

    private static readonly DayOfWeek[] Workweek =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static string Write(RepeatRule rule) => RepeatWords.Write(rule, Today);

    // ---- Writing -----------------------------------------------------------------------------------

    [Fact]
    public void A_repeat_from_today_needs_no_start()
        => Assert.Equal("every day", Write(new RepeatRule(RepeatUnit.Day, Start: Today)));

    [Fact]
    public void Any_other_start_is_written_in_figures()
    {
        // Always, even where the words alone ought to begin there: Todoist can start "every 31st" a
        // month after the 31st that's coming. And in figures, since "3 Jan 2027" after a time has
        // its year read as another time.
        Assert.Equal("every 2 weeks starting 2027-01-03", Write(new RepeatRule(RepeatUnit.Week, 2, Start: new DateOnly(2027, 1, 3))));
        Assert.Equal("every sat starting 2026-10-31", Write(new RepeatRule(RepeatUnit.Week, Weekdays: [DayOfWeek.Saturday], Start: new DateOnly(2026, 10, 31))));
        Assert.Equal("every 31st starting 2026-10-31", Write(new RepeatRule(RepeatUnit.Month, Start: new DateOnly(2026, 10, 31))));
    }

    [Fact]
    public void Named_days_are_written_monday_first_with_a_time_after_them()
        => Assert.Equal(
            "every mon, fri at 20:00",
            Write(new RepeatRule(RepeatUnit.Week, Weekdays: [DayOfWeek.Friday, DayOfWeek.Monday], Time: new TimeOnly(20, 0))));

    [Fact]
    public void Monday_to_friday_is_every_weekday()
        => Assert.Equal("every weekday", Write(new RepeatRule(RepeatUnit.Week, Weekdays: Workweek)));

    [Fact]
    public void A_month_repeats_on_the_day_it_starts_on()
    {
        Assert.Equal("every 2nd starting 2026-11-02", Write(new RepeatRule(RepeatUnit.Month, Start: new DateOnly(2026, 11, 2))));
        Assert.Equal("every 11th starting 2026-11-11", Write(new RepeatRule(RepeatUnit.Month, Start: new DateOnly(2026, 11, 11))));
    }

    [Fact]
    public void A_year_repeats_on_the_day_it_starts_on()
        => Assert.Equal("every 14 jan starting 2027-01-14", Write(new RepeatRule(RepeatUnit.Year, Start: new DateOnly(2027, 1, 14))));

    [Fact]
    public void Counted_from_completion_is_every_with_a_mark()
        => Assert.Equal("every! 3 months", Write(new RepeatRule(RepeatUnit.Month, 3, FromCompletion: true)));

    [Fact]
    public void Named_days_are_dropped_where_todoist_has_no_words_for_them()
    {
        // "every 2 weeks on mon" isn't in Todoist's help, so a fortnightly repeat falls on the day
        // it starts, and one counted from completion has no days to name.
        Assert.Equal("every 2 weeks", Write(new RepeatRule(RepeatUnit.Week, 2, Weekdays: [DayOfWeek.Monday])));
        Assert.Equal("every! week", Write(new RepeatRule(RepeatUnit.Week, Weekdays: [DayOfWeek.Monday], FromCompletion: true)));
    }

    [Fact]
    public void An_end_is_written_in_figures()
        => Assert.Equal("every day until 2027-08-03", Write(new RepeatRule(RepeatUnit.Day, Until: new DateOnly(2027, 8, 3))));

    [Theory]

    // From 1 October, gone by the 27th: every day and every other day come round today, every
    // third day tomorrow.
    [InlineData(RepeatUnit.Day, 1, "2026-10-01", "every day")]
    [InlineData(RepeatUnit.Day, 2, "2026-10-01", "every 2 days")]
    [InlineData(RepeatUnit.Day, 3, "2026-10-01", "every 3 days starting 2026-10-28")]
    [InlineData(RepeatUnit.Week, 2, "2026-10-01", "every 2 weeks starting 2026-10-29")]
    [InlineData(RepeatUnit.Month, 1, "2026-09-24", "every 24th starting 2026-11-24")]
    [InlineData(RepeatUnit.Month, 2, "2026-09-15", "every 2 months starting 2026-11-15")]
    [InlineData(RepeatUnit.Year, 1, "2025-10-01", "every 1 oct starting 2027-10-01")]
    public void A_start_already_gone_becomes_the_next_time_it_comes_round(RepeatUnit unit, int interval, string start, string words)
        => Assert.Equal(words, Write(new RepeatRule(unit, interval, Start: DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture))));

    [Fact]
    public void Named_days_whose_start_has_gone_need_none()
        => Assert.Equal("every thu", Write(new RepeatRule(RepeatUnit.Week, Weekdays: [DayOfWeek.Thursday], Start: new DateOnly(2026, 10, 1))));

    [Fact]
    public void The_start_can_be_left_out_while_still_naming_the_day()
    {
        // For words sent ahead of the day they start on, which a month still needs the date of.
        var rule = new RepeatRule(RepeatUnit.Month, Start: new DateOnly(2026, 11, 24), Until: new DateOnly(2027, 6, 1));

        Assert.Equal("every 24th until 2027-06-01", RepeatWords.Write(rule, Today, withStart: false));
    }

    // ---- Reading -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("every day")]
    [InlineData("every 3 days")]
    [InlineData("every week")]
    [InlineData("every 2 weeks starting 2027-01-03")]
    [InlineData("every mon, fri at 20:00")]
    [InlineData("every weekday")]
    [InlineData("every sat starting 2026-11-07")]
    [InlineData("every 24th starting 2026-12-24")]
    [InlineData("every 14 jan starting 2027-01-14")]
    [InlineData("every! 3 months")]
    [InlineData("every day at 09:30 until 2027-08-03")]
    public void What_is_written_reads_back_as_itself(string words)
        => Assert.Equal(words, Write(RepeatWords.Read(words, Today)!));

    [Theory]
    [InlineData("Every Saturday", "every sat")]
    [InlineData("everyday", "every day")]
    [InlineData("daily", "every day")]
    [InlineData("weekly", "every week")]
    [InlineData("every workday", "every weekday")]
    [InlineData("every other week", "every 2 weeks")]
    [InlineData("every quarter", "every 3 months")]
    [InlineData("every monday and friday", "every mon, fri")]
    [InlineData("every jan 14th", "every 14 jan starting 2027-01-14")]
    [InlineData("every day at 9am", "every day at 09:00")]
    [InlineData("every day at 12:30pm", "every day at 12:30")]
    [InlineData("every fri at noon", "every fri at 12:00")]
    [InlineData("everyday until aug 3", "every day until 2027-08-03")]
    [InlineData("everyday from 10 May until 20 May", "every day starting 2027-05-10 until 2027-05-20")]
    [InlineData("every 2 weeks starting 3 Jan 2027", "every 2 weeks starting 2027-01-03")]

    // Read by the box's own rules, so its other spellings count here too.
    [InlineData("every tues, thurs", "every tue, thu")]
    [InlineData("every 14 sept", "every 14 sep starting 2027-09-14")]
    [InlineData("every day at 4:30pm", "every day at 16:30")]

    // As Todoist hands them back: "@" for "at", and an end as "ending" in figures.
    [InlineData("every tue, fri @ 09:30", "every tue, fri at 09:30")]
    [InlineData("every tue, fri at 09:30 ending 2026-12-31", "every tue, fri at 09:30 until 2026-12-31")]
    public void Todoists_other_ways_of_saying_the_same_thing_read_as_it(string typed, string written)
        => Assert.Equal(written, Write(RepeatWords.Read(typed, Today)!));

    [Theory]

    // Real repeats Todoist reads, which this doesn't take apart rather than take wrongly.
    [InlineData("every other fri")]
    [InlineData("every last workday")]
    [InlineData("every 2, 15, 27")]
    [InlineData("every 12 hours starting at 9pm")]
    [InlineData("every mon at 8pm, tue at 9pm")]
    [InlineData("jeden Tag")]

    // Not English, or not a repeat.
    [InlineData("every 2 week")]
    [InlineData("every days")]
    [InlineData("every day at 9")]
    [InlineData("tomorrow")]
    [InlineData("every")]
    [InlineData("")]
    public void Anything_else_reads_as_nothing(string words)
        => Assert.Null(RepeatWords.Read(words, Today));

    // ---- The start Todoist keeps in the due date ---------------------------------------------------

    [Fact]
    public void Words_with_no_start_begin_on_the_day_the_task_is_due()
    {
        // Todoist kept "every day starting 30 Oct 2026" as "every day", due on the 30th.
        var rule = RepeatWords.Read("every day", Today, due: new DateOnly(2026, 10, 30))!;

        Assert.Equal(new DateOnly(2026, 10, 30), rule.Start);
    }

    [Fact]
    public void A_month_begins_on_the_due_date_even_when_it_has_been_put_off()
    {
        var rule = RepeatWords.Read("every 24th", Today, due: new DateOnly(2026, 12, 24))!;

        Assert.Equal(new DateOnly(2026, 12, 24), rule.Start);
    }

    [Fact]
    public void A_due_date_the_repeat_cannot_fall_on_is_no_start()
    {
        // Moved off its days by hand, as Todoist's scheduler allows: Saturdays, due on a Tuesday.
        Assert.Null(RepeatWords.Read("every sat", Today, due: new DateOnly(2026, 11, 3))!.Start);
        Assert.Equal(new DateOnly(2026, 11, 24), RepeatWords.Read("every 24th", Today, due: new DateOnly(2026, 11, 3))!.Start);
    }

    [Fact]
    public void Only_a_start_the_words_give_counts_as_given()
    {
        RepeatWords.Read("every 24th until 2027-06-01", Today, null, out var invented);
        RepeatWords.Read("every 24th starting 2026-12-24", Today, null, out var given);

        Assert.False(invented);
        Assert.True(given);
    }

    [Fact]
    public void A_start_the_words_give_beats_the_due_date()
    {
        var rule = RepeatWords.Read("every day starting 2026-11-10", Today, due: new DateOnly(2026, 10, 30))!;

        Assert.Equal(new DateOnly(2026, 11, 10), rule.Start);
    }
}
