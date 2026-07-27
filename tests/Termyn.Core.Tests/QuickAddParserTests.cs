using Termyn.Core.Capture;
using Termyn.Core.Model;
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
    public void A_bare_time_means_today()
    {
        var parse = Parse("Standup 9:15");

        Assert.Equal(Today, parse.DueDate);
        Assert.Equal(new TimeOnly(9, 15), parse.DueTime);
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

    [Fact]
    public void Reminders_are_kept_as_text_and_flagged()
    {
        var parse = Parse("Leave for airport !30m");

        Assert.Equal("Leave for airport !30m", parse.Content);
        Assert.Contains("!30m", parse.Unsupported);
    }

    [Fact]
    public void Assignees_are_dropped_and_flagged()
    {
        var parse = Parse("Review PR +alice");

        Assert.Equal("Review PR", parse.Content);
        Assert.Contains("+alice", parse.Unsupported);
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
        Assert.Equal("Plan tomorrow", parse.Content); // the second date is left as text
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
    public void Handles_empty_input()
    {
        var parse = Parse("   ");

        Assert.Equal(string.Empty, parse.Content);
        Assert.Null(parse.DueDate);
    }

    private static QuickAddParse Parse(string text)
        => new QuickAddParser(new FixedClock(Today)).Parse(text);
}
