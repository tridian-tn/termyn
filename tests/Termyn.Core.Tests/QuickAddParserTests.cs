using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

public class QuickAddParserTests
{
    // Friday 2026-07-31, so weekday maths is deterministic.
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public void Extracts_project_section_labels_and_priority()
    {
        var parse = Parse("Email finance report #Work /Reports @followup @urgent p1");

        Assert.Equal("Email finance report", parse.Content);
        Assert.Equal("Work", parse.ProjectName);
        Assert.Equal("Reports", parse.SectionName);
        Assert.Equal(new[] { "followup", "urgent" }, parse.Labels.ToArray());
        Assert.Equal(Priority.P1, parse.Priority);
    }

    [Fact]
    public void Defaults_to_lowest_priority_and_no_due_date()
    {
        var parse = Parse("Buy milk");

        Assert.Equal("Buy milk", parse.Content);
        Assert.Equal(Priority.P4, parse.Priority);
        Assert.Null(parse.DueDate);
        Assert.Null(parse.DueTime);
        Assert.Empty(parse.Unsupported);
    }

    [Theory]
    [InlineData("today", 2026, 7, 31)]
    [InlineData("tomorrow", 2026, 8, 1)]
    [InlineData("tom", 2026, 8, 1)]
    [InlineData("TOM", 2026, 8, 1)]
    [InlineData("2026-12-25", 2026, 12, 25)]
    [InlineData("friday", 2026, 7, 31)]   // today already matches
    [InlineData("monday", 2026, 8, 3)]    // the coming Monday
    [InlineData("SUNDAY", 2026, 8, 2)]
    public void Parses_the_supported_date_forms(string token, int year, int month, int day)
    {
        var parse = Parse($"Renew domain {token}");

        Assert.Equal(new DateOnly(year, month, day), parse.DueDate);
        Assert.Equal("Renew domain", parse.Content);
    }

    [Theory]
    [InlineData("I sat down")]
    [InlineData("Buy sun cream")]
    [InlineData("Book a wed venue")]
    public void Weekday_abbreviations_are_ordinary_words_not_dates(string text)
    {
        var parse = Parse(text);

        Assert.Equal(text, parse.Content);
        Assert.Null(parse.DueDate);
    }

    [Theory]
    [InlineData("4pm", 16, 0)]
    [InlineData("4:30pm", 16, 30)]
    [InlineData("16:30", 16, 30)]
    [InlineData("12am", 0, 0)]
    [InlineData("12pm", 12, 0)]
    public void Parses_a_time_of_day(string token, int hour, int minute)
    {
        var parse = Parse($"Call Bob tomorrow {token}");

        Assert.Equal(new TimeOnly(hour, minute), parse.DueTime);
        Assert.Equal(new DateOnly(2026, 8, 1), parse.DueDate);
        Assert.Equal("Call Bob", parse.Content);
    }

    [Fact]
    public void A_bare_time_still_to_come_means_today()
    {
        // The test clock stands at midday; one already gone is tomorrow, in the grammar's own tests.
        var parse = Parse("Standup 16:15");

        Assert.Equal(Today, parse.DueDate);
        Assert.Equal(new TimeOnly(16, 15), parse.DueTime);
    }

    [Fact]
    public void A_bare_number_is_content_not_a_time()
    {
        var parse = Parse("Buy 4 apples");

        Assert.Equal("Buy 4 apples", parse.Content);
        Assert.Null(parse.DueTime);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("13pm")]
    [InlineData("0:70")]
    [InlineData("2026-13-45")]
    [InlineData("2026-02-30")]
    public void Malformed_dates_and_times_stay_in_the_content(string token)
    {
        var parse = Parse($"Task {token}");

        Assert.Equal($"Task {token}", parse.Content);
        Assert.Null(parse.DueDate);
        Assert.Null(parse.DueTime);
    }

    [Theory]
    [InlineData("Water plants every weekday", "Water plants every weekday")]
    [InlineData("Water plants every friday", "Water plants every friday")]
    [InlineData("Standup every monday 9am", "Standup every monday 9am")]
    public void Recurrence_is_kept_verbatim_and_never_becomes_a_due_date(string text, string expectedContent)
    {
        var parse = Parse(text);

        Assert.Equal(expectedContent, parse.Content);
        Assert.Null(parse.DueDate);
        Assert.Null(parse.DueTime);
        Assert.NotEmpty(parse.Unsupported);
    }

    [Fact]
    public void Tokens_after_a_recurrence_phrase_are_still_parsed()
    {
        var parse = Parse("Water plants every friday #Home @garden");

        Assert.Equal("Water plants every friday", parse.Content);
        Assert.Equal("Home", parse.ProjectName);
        Assert.Equal("garden", parse.Labels.Single());
        Assert.Null(parse.DueDate);
    }

    [Fact]
    public void A_priority_written_after_a_recurrence_phrase_is_still_read()
    {
        var parse = Parse("Water plants every friday p1");

        Assert.Equal("Water plants every friday", parse.Content);
        Assert.Equal(Priority.P1, parse.Priority);
    }

    [Theory]
    [InlineData("Leave for airport !30m", 2026, 7, 31, 12, 30)]   // the clock stands at midday
    [InlineData("Call the hotel !2h30m", 2026, 7, 31, 14, 30)]
    [InlineData("Clear the trash !later", 2026, 7, 31, 16, 0)]    // ~4h on, floored to the hour
    [InlineData("Get groceries !6pm", 2026, 7, 31, 18, 0)]        // the next instance of 6pm
    [InlineData("Check the schedule !11am", 2026, 8, 1, 11, 0)]   // 11am has gone, so tomorrow's
    public void An_absolute_reminder_is_worked_out_off_the_clock(string text, int y, int mo, int d, int h, int mi)
    {
        var parse = Parse(text);

        var reminder = Assert.IsType<AbsoluteReminder>(parse.Reminders.Single());
        Assert.Equal(new DateOnly(y, mo, d), reminder.Date);
        Assert.Equal(new TimeOnly(h, mi), reminder.Time);
        Assert.Empty(parse.Unsupported);
    }

    [Theory]
    [InlineData("Leave !2h", 3, 29, 3, 30)]     // the clocks go forward at one, so it's half three
    [InlineData("Leave !2h", 10, 25, 1, 30)]    // and back at two, so it's half one again
    [InlineData("Leave !1hb", 3, 29, 2, 30)]    // no due time: the same span from now
    public void A_reminder_a_span_from_now_counts_the_hours_that_pass_across_a_change_of_clocks(string text, int mo, int d, int h, int mi)
    {
        // Half past midnight in London, the night the clocks change. Added to the wall clock, two
        // hours from now came out an hour out either way.
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var midnight = new DateTime(2026, mo, d, 0, 30, 0);
        var clock = new At(new DateTimeOffset(midnight, london.GetUtcOffset(midnight)));

        var parse = new QuickAddParser(clock).Parse(text, new DateSettings(london));

        var reminder = Assert.IsType<AbsoluteReminder>(parse.Reminders.Single());
        Assert.Equal(new DateOnly(2026, mo, d), reminder.Date);
        Assert.Equal(new TimeOnly(h, mi), reminder.Time);
    }

    /// <summary>A clock stopped at one instant.</summary>
    private sealed class At(DateTimeOffset now) : IClock
    {
        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);

        public DateTimeOffset UtcNow => now.ToUniversalTime();
    }

    [Theory]
    [InlineData("Dentist !tmr 3pm", 2026, 8, 1, 15, 0)]
    [InlineData("Review notes !Mon 9am", 2026, 8, 3, 9, 0)]      // the coming Monday
    [InlineData("Water the plants !tomorrow", 2026, 8, 1, 9, 0)] // a day with no time fires at nine
    public void A_reminder_can_name_a_day_and_time_of_its_own(string text, int y, int mo, int d, int h, int mi)
    {
        var parse = Parse(text);

        var reminder = Assert.IsType<AbsoluteReminder>(parse.Reminders.Single());
        Assert.Equal(new DateOnly(y, mo, d), reminder.Date);
        Assert.Equal(new TimeOnly(h, mi), reminder.Time);
    }

    [Theory]
    [InlineData("Prepare agenda tomorrow 11am !1hb", 60)]
    [InlineData("Prepare agenda tomorrow 11am !1h before", 60)]
    [InlineData("Prepare agenda tomorrow 11am !30mb", 30)]
    [InlineData("Prepare agenda tomorrow 11am !0mb", 0)]
    public void A_before_due_reminder_on_a_timed_task_is_relative(string text, int minutes)
    {
        var parse = Parse(text);

        var reminder = Assert.IsType<RelativeReminder>(parse.Reminders.Single());
        Assert.Equal(minutes, reminder.MinutesBefore);
    }

    [Fact]
    public void A_before_due_reminder_without_a_due_time_falls_back_to_a_span_from_now()
    {
        // Todoist's own rule: with no due time to count back from, "!1h before" is an hour from now.
        var parse = Parse("Buy milk !1hb");

        Assert.Null(parse.DueTime);
        var reminder = Assert.IsType<AbsoluteReminder>(parse.Reminders.Single());
        Assert.Equal(Today, reminder.Date);
        Assert.Equal(new TimeOnly(13, 0), reminder.Time);
    }

    [Fact]
    public void A_reminder_is_taken_out_of_the_content()
    {
        var parse = Parse("Leave for airport !30m");

        Assert.Equal("Leave for airport", parse.Content);
        Assert.Empty(parse.Unsupported);
    }

    [Fact]
    public void Several_reminders_are_each_read()
    {
        var parse = Parse("Submit report tomorrow 11am !1hb !3hb");

        Assert.Equal("Submit report", parse.Content);
        Assert.Collection(
            parse.Reminders,
            r => Assert.Equal(60, Assert.IsType<RelativeReminder>(r).MinutesBefore),
            r => Assert.Equal(180, Assert.IsType<RelativeReminder>(r).MinutesBefore));
    }

    // Every schedule here was sent to a live account as a reminder_add and taken (23 Oct 2026).
    [Theory]
    [InlineData("End of day review !every 5pm", "every 5pm")]
    [InlineData("Pills !every 16:30", "every 16:30")]
    [InlineData("Call mum !every sat 9am", "every sat 9am")]
    [InlineData("Call mum !every Sat 9AM", "every Sat 9AM")]   // sent as typed, case and all
    [InlineData("Review !every monday 9am", "every monday 9am")]
    [InlineData("Sync !every tue at 17:30", "every tue at 17:30")]
    [InlineData("Standup !every weekday at 9:30am", "every weekday at 9:30am")]
    [InlineData("Timesheet !every workday 9am", "every workday 9am")]
    [InlineData("Lie in !every weekend 10am", "every weekend 10am")]
    [InlineData("Plan !every day 9am", "every day 9am")]
    [InlineData("Plan !every week 9am", "every week 9am")]
    [InlineData("Rent !every month 9am", "every month 9am")]
    [InlineData("Bins !every other sat 9am", "every other sat 9am")]
    [InlineData("Plants !every other day 9am", "every other day 9am")]
    [InlineData("Payroll !every 2 weeks 9am", "every 2 weeks 9am")]
    [InlineData("Water !every 2 days 9am", "every 2 days 9am")]
    [InlineData("Stretch !every 2 hours", "every 2 hours")]
    [InlineData("Stretch !every 1 hour", "every 1 hour")]
    [InlineData("Drink water !every hour", "every hour")]
    [InlineData("Journal !every evening", "every evening")]
    [InlineData("Plan the day !every morning", "every morning")]
    public void A_recurring_reminder_is_read_as_its_schedule(string text, string schedule)
    {
        var parse = Parse(text);

        var reminder = Assert.IsType<RecurringReminder>(parse.Reminders.Single());
        Assert.Equal(schedule, reminder.Schedule);
        Assert.DoesNotContain("!every", parse.Content);
        Assert.Empty(parse.Unsupported);
        Assert.False(parse.IsRecurrence);   // the task itself doesn't repeat, so the capture isn't refused
    }

    [Fact]
    public void The_words_after_a_recurring_reminder_are_the_tasks_again()
    {
        // Todoist refuses "every sat 9am call mum" outright, so the schedule has to stop where it does.
        var parse = Parse("!every sat 9am call mum tomorrow #Home");

        Assert.Equal("every sat 9am", Assert.IsType<RecurringReminder>(parse.Reminders.Single()).Schedule);
        Assert.Equal("call mum", parse.Content);
        Assert.Equal(new DateOnly(2026, 8, 1), parse.DueDate);
        Assert.Equal("Home", parse.ProjectName);
    }

    [Fact]
    public void A_time_after_an_hourly_reminder_is_the_tasks_due_time()
    {
        // An hourly schedule takes no time, so one after it is read as the task's, the same as after "!30m".
        var parse = Parse("Stretch !every 2 hours tomorrow 9am");

        Assert.Equal("every 2 hours", Assert.IsType<RecurringReminder>(parse.Reminders.Single()).Schedule);
        Assert.Equal(new TimeOnly(9, 0), parse.DueTime);
    }

    // The first five were refused live: four for naming no time, and minutes as not a schedule at all.
    [Theory]
    [InlineData("Review !every monday", "!every monday")]
    [InlineData("Review !every other monday", "!every other monday")]
    [InlineData("Timesheet !every workday", "!every workday")]
    [InlineData("Payday !every 3rd friday 9am", "!every 3rd friday 9am")]
    [InlineData("Check !every 30 minutes 9am", "!every 30 minutes 9am")]
    [InlineData("Plan !every day report", "!every day report")]
    [InlineData("Stretch !every 2 hour", "!every 2 hour")]
    [InlineData("Water !every 0 days 9am", "!every 0 days 9am")]
    [InlineData("Water !every 01 day 9am", "!every 01 day 9am")]
    [InlineData("Review !every at 9am", "!every at 9am")]
    [InlineData("Water !every other hour", "!every other hour")]
    [InlineData("Review !every sat 9 am", "!every sat 9 am")]
    public void A_recurring_reminder_it_cannot_read_is_left_to_the_server_and_flagged(string text, string run)
    {
        var parse = Parse(text);

        Assert.Empty(parse.Reminders);
        Assert.False(parse.IsRecurrence);
        Assert.Contains(run, parse.Unsupported);
        Assert.Contains(run, parse.Content);
        Assert.Null(parse.DueTime);   // the run is taken whole, so a time in it can't become the task's
    }

    [Fact]
    public void With_smart_dates_off_a_recurring_reminder_is_left_in_the_words()
    {
        var parser = new QuickAddParser(new FixedClock(Today));
        var parse = parser.Parse("Call mum !every sat 9am", new DateSettings(TimeZoneInfo.Utc, InlineDatesOff: true));

        Assert.Empty(parse.Reminders);
        Assert.Contains("!every sat 9am", parse.Unsupported);
        Assert.Equal("Call mum !every sat 9am", parse.Content);
    }

    [Fact]
    public void For_an_account_that_reads_another_language_a_recurring_reminder_is_left_in_the_words()
    {
        // The schedule goes to the server in English, so it's only read for an account that reads English.
        var parser = new QuickAddParser(new FixedClock(Today));
        var parse = parser.Parse("Call mum !every sat 9am", new DateSettings(TimeZoneInfo.Utc, Language: "de"));

        Assert.Empty(parse.Reminders);
        Assert.Contains("!every sat 9am", parse.Unsupported);
    }

    [Fact]
    public void A_reminder_shortcut_it_cannot_read_stays_in_the_words_and_is_flagged()
    {
        var parse = Parse("Some task !wat");

        Assert.Empty(parse.Reminders);
        Assert.Contains("!wat", parse.Unsupported);
        Assert.Contains("!wat", parse.Content);
    }

    [Fact]
    public void With_smart_dates_off_a_reminder_is_left_in_the_words()
    {
        // The "!" is read on the same setting a date is: for an account that reads none, it's left
        // whole rather than guessed at, the safe direction when the apps' behaviour isn't settled.
        var parser = new QuickAddParser(new FixedClock(Today));
        var parse = parser.Parse("Leave for airport !30m", new DateSettings(TimeZoneInfo.Utc, InlineDatesOff: true));

        Assert.Empty(parse.Reminders);
        Assert.Equal("Leave for airport !30m", parse.Content);
        Assert.Contains("!30m", parse.Unsupported);
    }

    [Fact]
    public void A_plus_word_stays_in_the_title()
    {
        // Assigning isn't supported, and a capture is made from this parse, so reading "+alice" as
        // an assignee only took it off the task — and a phone number went with it.
        var parse = Parse("Call John +447700900123");

        Assert.Equal("Call John +447700900123", parse.Content);
        Assert.Empty(parse.Unsupported);
        Assert.Equal("Review PR +alice", Parse("Review PR +alice").Content);
    }

    [Fact]
    public void Unrecognised_tokens_stay_in_the_content()
    {
        var parse = Parse("Ship v1.2 p9 #Work");

        Assert.Equal("Ship v1.2 p9", parse.Content);
        Assert.Equal(Priority.P4, parse.Priority);
        Assert.Equal("Work", parse.ProjectName);
    }

    [Fact]
    public void The_first_of_each_repeated_token_wins_and_labels_are_deduplicated()
    {
        var parse = Parse("Plan today tomorrow #A #B /S1 /S2 @x @X");

        Assert.Equal(Today, parse.DueDate);
        Assert.Equal("A", parse.ProjectName);
        Assert.Equal("S1", parse.SectionName);
        Assert.Equal(new[] { "x" }, parse.Labels.ToArray());

        // Anything the first one already answered for is left as text, which used to be true of the
        // second date and not of the second project or section — those went from the task without
        // being applied to it. The repeated label is the one that is properly gone: it named a label
        // already on the task, so it was acted on rather than ignored.
        Assert.Equal("Plan tomorrow #B /S2", parse.Content);
    }

    [Fact]
    public void Splits_on_any_whitespace_not_just_spaces()
    {
        var parse = Parse("Meeting\ttomorrow  4pm");

        Assert.Equal("Meeting", parse.Content);
        Assert.Equal(new DateOnly(2026, 8, 1), parse.DueDate);
        Assert.Equal(new TimeOnly(16, 0), parse.DueTime);
    }

    [Fact]
    public void Text_made_only_of_tokens_leaves_no_content()
    {
        var parse = Parse("#Work p1");

        Assert.Equal(string.Empty, parse.Content);
        Assert.Equal("Work", parse.ProjectName);
    }

    [Fact]
    public void A_second_project_stays_in_the_words()
    {
        // The first one names the project. The second used to be dropped on the floor: not applied,
        // not flagged, and not in the content either — so a word the user typed and can see in the
        // box was simply not on the task afterwards.
        var parse = Parse("call mum #Work #Personal");

        Assert.Equal("Work", parse.ProjectName);
        Assert.Equal("call mum #Personal", parse.Content);
    }

    [Theory]
    [InlineData("Fix issue #42 p1", true, false, "Fix issue #42")]
    [InlineData("Read /dev docs #Work", false, true, "Read /dev docs")]
    [InlineData("Read /dev docs #Work", true, true, "Read /dev docs #Work")]
    [InlineData("Read /dev docs #Work", false, false, "Read docs")]
    [InlineData("Call #My\\ Project now", true, false, "Call #My\\ Project now")]
    public void A_place_can_be_written_back_where_it_was_typed(string text, bool project, bool section, string expected)
    {
        // For a capture naming a place there's no such thing as. Todoist keeps the word in the
        // title, and in the middle of a sentence is where it belongs.
        Assert.Equal(expected, Parse(text).TitleKeeping(project, section));
    }

    [Fact]
    public void A_place_typed_after_a_time_put_back_is_written_back_in_order()
    {
        // The time goes back into the words when it has no day to go on, ahead of the place.
        var parse = Parse("Plan fri 9am #Work");

        Assert.Equal("Plan fri 9am", parse.Content);
        Assert.Equal("Plan fri 9am #Work", parse.TitleKeeping(project: true, section: false));
    }

    [Fact]
    public void A_second_section_stays_in_the_words()
    {
        var parse = Parse("tidy /Later /Someday");

        Assert.Equal("Later", parse.SectionName);
        Assert.Equal("tidy /Someday", parse.Content);
    }

    [Fact]
    public void A_repeated_label_is_applied_once_and_not_written_twice()
    {
        // Not the same case, and it stays as it is: the second one names a label that is already on
        // the task, so it was acted on rather than ignored and belongs out of the words.
        var parse = Parse("water plants @home @home");

        Assert.Equal(["home"], parse.Labels);
        Assert.Equal("water plants", parse.Content);
    }

    [Fact]
    public void Handles_empty_input()
    {
        var parse = Parse("   ");

        Assert.Equal(string.Empty, parse.Content);
        Assert.Null(parse.DueDate);
    }

    [Fact]
    public void An_escaped_space_keeps_a_place_name_whole()
    {
        // Todoist's own way of naming a project with a space in it. Without it, "#My Project" is a
        // project called "My" and a title with "Project" in it.
        var parse = Parse(@"Buy milk #My\ Project /Next\ Week @on\ the\ way");

        Assert.Equal("My Project", parse.ProjectName);
        Assert.Equal("Next Week", parse.SectionName);
        Assert.Equal(["on the way"], parse.Labels);
        Assert.Equal("Buy milk", parse.Content);
    }

    [Fact]
    public void An_unescaped_space_still_ends_a_place_name()
    {
        var parse = Parse("Buy milk #My Project");

        Assert.Equal("My", parse.ProjectName);
        Assert.Equal("Buy milk Project", parse.Content);
    }

    [Fact]
    public void A_backslash_outside_a_place_name_joins_nothing()
    {
        // Joined on, "today" would be part of the word before it and the task would have no day.
        var parse = Parse(@"Clean C:\ today");

        Assert.Equal(@"Clean C:\", parse.Content);
        Assert.Equal(Today, parse.DueDate);
    }

    [Fact]
    public void A_second_escaped_project_stays_in_the_words_as_it_was_typed()
    {
        var parse = Parse(@"tidy #Home #Big\ Shed");

        Assert.Equal("Home", parse.ProjectName);
        Assert.Equal(@"tidy #Big\ Shed", parse.Content);
    }

    [Theory]
    [InlineData("Work")]
    [InlineData("My Project")]
    [InlineData("Two  Spaces")]
    public void An_escaped_name_reads_back_as_itself(string name)
    {
        var parse = Parse($"Buy milk #{QuickAddParser.Escaped(name)} /{QuickAddParser.Escaped(name)} @{QuickAddParser.Escaped(name)}");

        Assert.Equal(name, parse.ProjectName);
        Assert.Equal(name, parse.SectionName);
        Assert.Equal([name], parse.Labels);
        Assert.Equal("Buy milk", parse.Content);
    }

    /// <summary>Parses in UTC, where the test clock's midday is midday on every machine.</summary>
    private static QuickAddParse Parse(string text)
        => new QuickAddParser(new FixedClock(Today)).Parse(text, new DateSettings(TimeZoneInfo.Utc));
}
