using Termyn.Core.Capture;

namespace Termyn.Core.Tests;

/// <summary>
/// A repeat written as Todoist's words and read back: only the forms its help on recurring dates
/// gives, and nothing it would have to guess at.
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
    public void Every_day_from_today_needs_no_start()
        => Assert.Equal("every day", Write(new RepeatRule(RepeatUnit.Day, Start: Today)));

    [Fact]
    public void A_start_is_written_only_where_the_words_would_begin_somewhere_else()
    {
        Assert.Equal("every 2 weeks starting 3 Jan 2027", Write(new RepeatRule(RepeatUnit.Week, 2, Start: new DateOnly(2027, 1, 3))));

        // The coming Saturday is where "every sat" begins anyway; the one after isn't.
        Assert.Equal("every sat", Write(new RepeatRule(RepeatUnit.Week, Weekdays: [DayOfWeek.Saturday], Start: new DateOnly(2026, 10, 31))));
        Assert.Equal("every sat starting 7 Nov 2026", Write(new RepeatRule(RepeatUnit.Week, Weekdays: [DayOfWeek.Saturday], Start: new DateOnly(2026, 11, 7))));
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
        Assert.Equal("every 31st", Write(new RepeatRule(RepeatUnit.Month, Start: new DateOnly(2026, 10, 31))));
        Assert.Equal("every 2nd", Write(new RepeatRule(RepeatUnit.Month, Start: new DateOnly(2026, 11, 2))));
        Assert.Equal("every 11th", Write(new RepeatRule(RepeatUnit.Month, Start: new DateOnly(2026, 11, 11))));
    }

    [Fact]
    public void A_year_repeats_on_the_day_it_starts_on()
        => Assert.Equal("every 14 jan", Write(new RepeatRule(RepeatUnit.Year, Start: new DateOnly(2027, 1, 14))));

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
    public void An_end_is_written_with_its_year()
        => Assert.Equal("every day until 3 Aug 2027", Write(new RepeatRule(RepeatUnit.Day, Until: new DateOnly(2027, 8, 3))));

    // ---- Reading -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("every day")]
    [InlineData("every 3 days")]
    [InlineData("every week")]
    [InlineData("every 2 weeks starting 3 Jan 2027")]
    [InlineData("every mon, fri at 20:00")]
    [InlineData("every weekday")]
    [InlineData("every sat starting 7 Nov 2026")]
    [InlineData("every 24th")]
    [InlineData("every 14 jan")]
    [InlineData("every! 3 months")]
    [InlineData("every day at 09:30 until 3 Aug 2027")]
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
    [InlineData("every jan 14th", "every 14 jan")]
    [InlineData("every day at 9am", "every day at 09:00")]
    [InlineData("every day at 12:30pm", "every day at 12:30")]
    [InlineData("every fri at noon", "every fri at 12:00")]
    [InlineData("everyday until aug 3", "every day until 3 Aug 2027")]
    [InlineData("everyday from 10 May until 20 May", "every day starting 10 May 2027 until 20 May 2027")]
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
}
