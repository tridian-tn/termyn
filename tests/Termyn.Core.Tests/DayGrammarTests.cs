using Termyn.Core.Capture;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// The grammar a day is read with, wherever one is typed: in quick-add text, and in a box that asks
/// for nothing else.
/// </summary>
/// <remarks>
/// Every form is one Todoist reads, and is expected to read the same way, so the cases here follow
/// Todoist's own help page on dates where it gives one. Read in UTC, where the test clock's midday
/// is midday, so a bare time's "already gone" is the same on every machine that runs this.
/// </remarks>
public class DayGrammarTests
{
    // Friday 2026-07-31, so weekday maths is deterministic.
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>Nothing known about the account, read in UTC.</summary>
    private static readonly DateSettings Unknown = new(TimeZoneInfo.Utc);

    /// <summary>An account that's said everything, with Todoist's defaults: day first, weeks from Monday.</summary>
    private static readonly DateSettings Told = new(TimeZoneInfo.Utc, DayFirst: true, WeekStart: DayOfWeek.Monday, NextWeek: DayOfWeek.Monday);

    // ---- Days by name ------------------------------------------------------------------------------

    [Theory]
    [InlineData("4 aug", 2026, 8, 4)]
    [InlineData("aug 4", 2026, 8, 4)]
    [InlineData("4th aug", 2026, 8, 4)]
    [InlineData("Aug 4th", 2026, 8, 4)]
    [InlineData("4 August", 2026, 8, 4)]
    [InlineData("4 August 2027", 2027, 8, 4)]
    [InlineData("August 4 2027", 2027, 8, 4)]
    [InlineData("Aug 4, 2027", 2027, 8, 4)]
    [InlineData("sept 1", 2026, 9, 1)]
    [InlineData("31 jul", 2026, 7, 31)]   // today counts
    [InlineData("30 jul", 2027, 7, 30)]   // gone this year, so next year's
    [InlineData("29 feb", 2028, 2, 29)]   // the next year that has one
    public void A_day_and_month_are_read_either_way_round(string words, int year, int month, int day)
    {
        var parse = Parse($"Renew domain {words}", Unknown);

        Assert.Equal(new DateOnly(year, month, day), parse.DueDate);
        Assert.Equal("Renew domain", parse.Content);
    }

    [Theory]
    [InlineData("Renew domain 31 apr")]
    [InlineData("Renew domain 29 feb 2027")]
    [InlineData("Buy 4 apples")]
    [InlineData("Call aug 4, then mum")]
    public void Words_that_only_look_like_a_day_stay_in_the_task(string text)
    {
        // No year has 31 April, 2027 hasn't a 29 February, apples aren't a month, and a comma after
        // the day belongs to the sentence unless a year follows it.
        var parse = Parse(text, Unknown);

        Assert.Equal(text, parse.Content);
        Assert.Null(parse.DueDate);
    }

    [Theory]
    [InlineData("tod", 2026, 7, 31)]
    [InlineData("next month", 2026, 8, 31)]
    [InlineData("next year", 2027, 1, 1)]
    [InlineData("end of month", 2026, 7, 31)]
    [InlineData("in 3 days", 2026, 8, 3)]
    [InlineData("in 1 day", 2026, 8, 1)]
    [InlineData("in 2 weeks", 2026, 8, 14)]
    [InlineData("in 6 months", 2027, 1, 31)]
    [InlineData("this fri", 2026, 7, 31)]
    [InlineData("this monday", 2026, 8, 3)]
    public void The_phrases_Todoist_reads_are_read_the_same_way(string words, int year, int month, int day)
    {
        var parse = Parse($"Pay rent {words}", Unknown);

        Assert.Equal(new DateOnly(year, month, day), parse.DueDate);
        Assert.Equal("Pay rent", parse.Content);
    }

    [Theory]
    [InlineData("Pay rent in three days")]
    [InlineData("Pay rent in 3 apples")]
    [InlineData("Pay rent end of term")]
    public void A_phrase_that_doesnt_finish_isnt_a_day(string text)
    {
        var parse = Parse(text, Unknown);

        Assert.Equal(text, parse.Content);
        Assert.Null(parse.DueDate);
    }

    // ---- What turns on the account's settings ----------------------------------------------------

    [Theory]
    [InlineData("next monday", 2026, 8, 3)]
    [InlineData("next friday", 2026, 8, 7)]   // not today, though today is a Friday
    [InlineData("next sun", 2026, 8, 9)]     // this week's Sunday is still to come, and skipped
    public void Next_and_a_weekday_is_that_day_of_the_following_week(string words, int year, int month, int day)
    {
        var parse = Parse($"Plan {words}", Told);

        Assert.Equal(new DateOnly(year, month, day), parse.DueDate);
        Assert.Equal("Plan", parse.Content);
    }

    [Fact]
    public void Next_and_a_weekday_follows_Todoists_own_example()
    {
        // From its help page: on Monday 20 October, "next Tuesday" skips the 21st for the 28th.
        var parser = new QuickAddParser(new FixedClock(new DateOnly(2025, 10, 20)));

        Assert.Equal(new DateOnly(2025, 10, 28), parser.Parse("next tuesday", Told).DueDate);
    }

    [Fact]
    public void Where_the_week_starts_decides_which_week_is_next()
    {
        // From Friday, a week that starts on Sunday has already begun the next one by the 2nd.
        var sundays = Told with { WeekStart = DayOfWeek.Sunday };

        Assert.Equal(new DateOnly(2026, 8, 9), Parse("next sunday", Told).DueDate);
        Assert.Equal(new DateOnly(2026, 8, 2), Parse("next sunday", sundays).DueDate);
    }

    [Fact]
    public void Next_and_a_weekday_isnt_read_without_knowing_where_the_week_starts()
    {
        // Left for the server, which knows. And the weekday isn't read on its own either: that would
        // be this week's Friday, the one day "next friday" can't mean.
        var parse = Parse("Plan next friday", Unknown);

        Assert.Equal("Plan next friday", parse.Content);
        Assert.Null(parse.DueDate);
    }

    [Fact]
    public void Next_week_is_the_day_the_account_puts_things_off_until()
    {
        var wednesdays = Told with { NextWeek = DayOfWeek.Wednesday };

        Assert.Equal(new DateOnly(2026, 8, 3), Parse("Plan next week", Told).DueDate);
        Assert.Equal(new DateOnly(2026, 8, 5), Parse("Plan next week", wednesdays).DueDate);
        Assert.Null(Parse("Plan next week", Unknown).DueDate);
    }

    [Theory]
    [InlineData(true, 2027, 6, 5)]
    [InlineData(false, 2027, 5, 6)]
    public void Figures_that_could_be_either_are_read_in_the_accounts_order(bool dayFirst, int year, int month, int day)
    {
        var parse = Parse("Renew 5/6", Told with { DayFirst = dayFirst });

        Assert.Equal(new DateOnly(year, month, day), parse.DueDate);
    }

    [Fact]
    public void Figures_that_could_be_either_are_left_alone_when_the_order_isnt_known()
    {
        var parse = Parse("Renew 5/6", Unknown);

        Assert.Equal("Renew 5/6", parse.Content);
        Assert.Null(parse.DueDate);
    }

    [Theory]
    [InlineData("25/12", 2026, 12, 25)]
    [InlineData("12/25", 2026, 12, 25)]
    [InlineData("5/5", 2027, 5, 5)]
    [InlineData("25/12/2027", 2027, 12, 25)]
    [InlineData("12/25/2027", 2027, 12, 25)]
    [InlineData("2027/12/25", 2027, 12, 25)]
    [InlineData("2027-12-25", 2027, 12, 25)]
    public void Figures_only_one_way_round_can_be_are_read_whatever_the_order(string words, int year, int month, int day)
    {
        // Lenient, as Todoist is: a 25 can only be the day, so the account's order doesn't come in.
        Assert.Equal(new DateOnly(year, month, day), Parse($"Renew {words}", Unknown).DueDate);
        Assert.Equal(new DateOnly(year, month, day), Parse($"Renew {words}", Told with { DayFirst = false }).DueDate);
    }

    [Theory]
    [InlineData("13/13")]
    [InlineData("31/4")]
    [InlineData("0/5")]
    public void Figures_no_way_round_can_be_stay_in_the_task(string words)
    {
        Assert.Null(Parse($"Renew {words}", Told).DueDate);
    }

    // ---- Times -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Meet Sam at 4pm")]
    [InlineData("Meet Sam tomorrow at 16:00")]
    public void At_before_a_time_goes_with_it(string text)
    {
        var parse = Parse(text, Unknown);

        Assert.Equal("Meet Sam", parse.Content);
        Assert.Equal(new TimeOnly(16, 0), parse.DueTime);
    }

    [Fact]
    public void At_before_anything_else_is_a_word()
    {
        Assert.Equal("Look at the garden", Parse("Look at the garden", Unknown).Content);
    }

    [Fact]
    public void A_bare_time_still_to_come_is_today()
    {
        // The test clock stands at midday.
        var parse = Parse("Standup 16:00", Unknown);

        Assert.Equal(Today, parse.DueDate);
    }

    [Fact]
    public void A_bare_time_already_gone_is_tomorrow()
    {
        // Todoist's rule: "6pm" typed at seven is tomorrow evening, not an evening already past.
        var parse = Parse("Standup 9:15", Unknown);

        Assert.Equal(Today.AddDays(1), parse.DueDate);
        Assert.Equal(new TimeOnly(9, 15), parse.DueTime);
    }

    [Fact]
    public void Today_is_the_accounts_today()
    {
        // At midday in London it's already tomorrow in Auckland, and that's the day "today" means to
        // an account that lives there — as it does to the server reading the same words.
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

        Assert.Equal(Today.AddDays(1), Parse("Pay today", new DateSettings(auckland)).DueDate);
    }

    [Theory]
    [InlineData("Plan next friday 9am")]
    [InlineData("Plan next week at 9am")]
    [InlineData("Renew 5/6 9am")]
    [InlineData("Submit report fri 5pm")]
    [InlineData("Submit report 5pm fri")]
    [InlineData("Submit report fri at 5pm")]
    [InlineData("Party next weekend 8pm")]
    [InlineData("Party this weekend 8pm")]
    public void A_time_beside_a_day_that_wasnt_read_stays_with_it(string text)
    {
        // Today's nine o'clock would be a day Todoist never gives it, so the time stays in the words
        // with its day, for the server to read the two together.
        var parse = Parse(text, Unknown);

        Assert.Equal(text, parse.Content);
        Assert.Null(parse.DueDate);
        Assert.Null(parse.DueTime);
    }

    [Fact]
    public void A_time_beside_a_day_that_was_read_goes_on_it()
    {
        var parse = Parse("Plan next friday 9am", Told);

        Assert.Equal(new DateOnly(2026, 8, 7), parse.DueDate);
        Assert.Equal(new TimeOnly(9, 0), parse.DueTime);
        Assert.Equal("Plan", parse.Content);
    }

    [Fact]
    public void A_shortened_weekday_that_isnt_beside_the_time_leaves_it_alone()
    {
        // "sun" here is the cream, not Sunday: only a weekday right beside a time is taken for its day.
        var parse = Parse("Buy sun cream 4pm", Unknown);

        Assert.Equal("Buy sun cream", parse.Content);
        Assert.Equal(new TimeOnly(16, 0), parse.DueTime);
        Assert.Equal(Today, parse.DueDate);
    }

    [Theory]
    [InlineData("５")]
    [InlineData("４ aug")]
    [InlineData("aug ４")]
    [InlineData("٤pm")]
    [InlineData("２５/12")]
    [InlineData("2027/１/2")]
    [InlineData("in ３ days")]
    public void Digits_from_other_scripts_are_words_rather_than_a_crash(string words)
    {
        // \d takes in every script's digits and int.Parse throws on them. The date box reads on every
        // keystroke, so a full-width digit from an IME brought up the unhandled-exception dialog.
        Assert.Equal(DayReading.Unread, Read(words, Told));
        Assert.Null(Parse($"Task {words}", Told).DueDate);
    }

    // ---- Repeats -----------------------------------------------------------------------------------

    [Fact]
    public void Every_with_a_bang_is_a_repeat_too()
    {
        // Todoist's form for a repeat counted from when the task is done rather than when it was due.
        var parse = Parse("Water plants every! 3 days", Unknown);

        Assert.True(parse.IsRecurrence);
        Assert.Null(parse.DueDate);
        Assert.Equal("Water plants every! 3 days", parse.Content);
    }

    // ---- A box asking for a day ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_reads_as_blank(string text)
    {
        Assert.Equal(DayReading.Blank, Read(text, Unknown));
    }

    [Theory]
    [InlineData("every monday")]
    [InlineData("every! 3 days")]
    [InlineData("daily 9am")]
    [InlineData("each monday")]
    [InlineData("Weekly friday")]
    public void A_schedule_reads_as_a_repeat(string text)
    {
        Assert.Equal(DayReading.Repeat, Read(text, Unknown));
    }

    [Theory]
    [InlineData("4 aug 2026 at 4pm", 2026, 8, 4, 16, 0)]
    [InlineData("16:00 tomorrow", 2026, 8, 1, 16, 0)]
    [InlineData("next friday 9am", 2026, 8, 7, 9, 0)]
    public void A_day_and_a_time_read_together(string text, int year, int month, int day, int hour, int minute)
    {
        Assert.Equal(DayReading.On(new DateOnly(year, month, day), new TimeOnly(hour, minute)), Read(text, Told));
    }

    [Fact]
    public void A_time_alone_reads_as_the_day_it_next_comes_round()
    {
        Assert.Equal(DayReading.On(Today, new TimeOnly(16, 0)), Read("4pm", Unknown));
        Assert.Equal(DayReading.On(Today.AddDays(1), new TimeOnly(9, 0)), Read("9am", Unknown));
    }

    [Theory]
    [InlineData("tomorrow p1")]
    [InlineData("in three weeks")]
    [InlineData("tomorrow tomorrow")]
    [InlineData("later this week")]
    [InlineData("next friday")]
    public void Anything_left_over_means_it_wasnt_read(string text)
    {
        // "next friday" with nothing known about where the week starts: the server reads that one.
        Assert.Equal(DayReading.Unread, Read(text, Unknown));
    }

    [Fact]
    public void A_box_reads_exactly_what_capture_would()
    {
        // The point of one grammar. Every form a capture takes as a day, the box takes as the same
        // one — checked across the forms rather than trusted to the two sharing a method.
        string[] forms =
            ["today", "tom", "friday", "this sat", "next fri", "next week", "next month", "in 4 weeks",
             "end of month", "4 aug", "aug 4th 2027", "25/12", "5/6", "2027-01-02", "2027/1/2"];

        foreach (var form in forms)
        {
            var captured = Parse($"Task {form}", Told).DueDate;
            Assert.Equal(DayReading.On(captured!.Value), Read(form, Told));
        }
    }

    [Fact]
    public void What_the_box_writes_for_a_day_it_reads_back_as_that_day()
    {
        // Every day of a leap year and the years either side, with and without a time — including
        // days already gone, which written without a year would read back as next year's.
        TimeOnly?[] times = [null, new TimeOnly(0, 0), new TimeOnly(9, 5), new TimeOnly(23, 59)];

        for (var day = new DateOnly(2027, 1, 1); day <= new DateOnly(2029, 12, 31); day = day.AddDays(1))
        {
            foreach (var time in times)
                Assert.Equal(DayReading.On(day, time), Read(QuickAddParser.Written(day, time), Unknown));
        }

        Assert.Equal(DayReading.On(new DateOnly(2020, 2, 29)), Read(QuickAddParser.Written(new DateOnly(2020, 2, 29)), Unknown));
    }

    [Fact]
    public void A_day_is_written_with_its_year_and_an_English_month()
    {
        Assert.Equal("4 Aug 2026", QuickAddParser.Written(new DateOnly(2026, 8, 4)));
        Assert.Equal("4 Aug 2026 07:05", QuickAddParser.Written(new DateOnly(2026, 8, 4), new TimeOnly(7, 5)));
    }

    private static QuickAddParse Parse(string text, DateSettings settings)
        => new QuickAddParser(new FixedClock(Today)).Parse(text, settings);

    private static DayReading Read(string text, DateSettings settings)
        => new QuickAddParser(new FixedClock(Today)).ReadDay(text, settings);
}
