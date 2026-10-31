using Termyn.Core.Capture;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The line under the capture box, held to the task it is describing.
/// </summary>
/// <remarks>
/// Its whole job is to say what pressing Enter will do, so what it says and what
/// <see cref="ItemFields.ForAdd"/> sends have to be the same answer. Both are asked here rather
/// than the wording alone, because the wording being wrong is only visible next to what it claims
/// to describe.
/// </remarks>
public class CapturePreviewTextTests
{
    private static readonly DateOnly Today = new(2026, 9, 7);

    private static QuickAddParse Parse(string text)
        => new QuickAddParser(new FixedClock(Today)).Parse(text);

    private static string Preview(string text, bool remindersAvailable = true)
        => CapturePreviewText.For(new CapturePreview(Parse(text), ProjectResolved: true, SectionResolved: true, RemindersAvailable: remindersAvailable));

    [Fact]
    public void A_repeating_task_is_not_promised_a_due_date()
    {
        // "every day p1 9am" — the priority ends the recurrence phrase, leaving a bare time the
        // parser reads as nine o'clock this morning. The task goes over with no due date at all,
        // which is deliberate, so a line saying it is due today at 09:00 describes a different task
        // from the one about to be created.
        var text = "every day p1 9am";

        Assert.DoesNotContain("09:00", Preview(text));
        Assert.DoesNotContain($"{Today:yyyy-MM-dd}", Preview(text));

        // The half that was always right: the schedule is still named, so nothing goes unsaid.
        Assert.Contains("every day", Preview(text));
    }

    [Fact]
    public void What_the_line_says_about_the_date_is_what_is_sent()
    {
        // The two read the same field and have to agree about it. Asked of both shapes: one that
        // repeats and one that doesn't.
        foreach (var text in new[] { "every day p1 9am", "pay rent 2026-10-01 every month", "buy milk tomorrow 4pm" })
        {
            var sends = ItemFields.ForAdd(Parse(text)).ContainsKey("due");
            var says = Preview(text).Contains("2026-", StringComparison.Ordinal);

            Assert.True(says == sends, $"'{text}': the line {(says ? "names" : "does not name")} a date and the task {(sends ? "gets" : "does not get")} one");
        }
    }

    [Fact]
    public void A_one_off_task_still_shows_its_date_and_time()
    {
        // The change is about recurrences alone. An ordinary due date is the commonest thing this
        // line has to say and it still says it.
        var preview = Preview("buy milk tomorrow 4pm");

        Assert.Contains("2026-09-08 16:00", preview);
    }

    [Fact]
    public void The_line_shows_what_was_left_in_the_words()
    {
        // A second project isn't applied, so it stays in the content — and the line quotes the
        // content, which is how someone looking at the box can see where it ended up.
        var preview = Preview("call mum #Work #Personal");

        Assert.Contains("\"call mum #Personal\"", preview);
        Assert.Contains("#Work", preview);
    }

    [Fact]
    public void An_unknown_project_says_the_word_stays_in_the_title()
    {
        // Not "goes to Inbox", which it never did: a capture naming no place it can find goes where
        // one naming none would, with the word left in its title.
        var preview = CapturePreviewText.For(
            new CapturePreview(Parse("call mum #Nowhere"), ProjectResolved: false, SectionResolved: true, RemindersAvailable: true));

        Assert.Contains("#Nowhere (no such project — kept in the title)", preview);
        Assert.DoesNotContain("Inbox", preview);
    }

    [Fact]
    public void The_empty_hint_reads_what_the_catalogue_says()
    {
        Assert.Equal("Add a task…  #project /section @label p1 tomorrow 4pm", CapturePreviewText.Hint(readsDays: true));
        Assert.Equal("Add a task…  #project /section @label p1", CapturePreviewText.Hint(readsDays: false));
    }

    [Fact]
    public void An_unknown_section_says_it_is_unknown()
    {
        var parse = Parse("call mum") with { SectionName = "Nowhere" };
        var preview = CapturePreviewText.For(new CapturePreview(parse, ProjectResolved: true, SectionResolved: false, RemindersAvailable: true));

        Assert.Contains("/Nowhere (no such section — kept in the title)", preview);
    }

    [Fact]
    public void A_field_that_needs_the_server_is_named_as_offline()
    {
        var parse = Parse("call mum") with { Unsupported = ["deadline", "assignee"] };
        var preview = CapturePreviewText.For(new CapturePreview(parse, ProjectResolved: true, SectionResolved: true, RemindersAvailable: true));

        Assert.Contains("(needs a connection: deadline, assignee)", preview);
    }

    [Fact]
    public void A_reminder_the_plan_takes_is_shown_as_one_it_will_set()
    {
        var preview = Preview("leave for airport !30m");

        Assert.Contains("remind", preview);
        Assert.DoesNotContain("!30m", preview);   // read, not left in the words
    }

    [Fact]
    public void A_before_due_reminder_reads_in_words()
    {
        var preview = Preview("submit report tomorrow 11am !1hb");

        Assert.Contains("remind 1h before", preview);
    }

    [Theory]
    [InlineData("submit report tomorrow 11am !0mb", "remind when due")]
    [InlineData("submit report tomorrow 11am !30mb", "remind 30m before")]
    [InlineData("submit report tomorrow 11am !3hb", "remind 3h before")]
    public void Each_kind_of_before_due_reminder_reads_what_the_catalogue_says(string text, string expected)
        => Assert.Contains(expected, Preview(text));

    [Fact]
    public void A_recurring_reminder_reads_as_its_schedule()
    {
        // The server works out when it next fires, so the preview can't name a moment, only the words.
        var preview = Preview("call mum !every sat 9am");

        Assert.Contains("remind every sat 9am", preview);
        Assert.DoesNotContain("needs a connection", preview);
    }

    [Fact]
    public void A_reminder_the_plan_wont_take_says_so_rather_than_setting_it()
    {
        var preview = Preview("leave for airport !30m", remindersAvailable: false);

        Assert.Contains("reminders aren't on this plan", preview);
        Assert.DoesNotContain("needs a connection", preview);
    }

    [Fact]
    public void A_recurring_capture_is_not_promised_a_reminder()
    {
        // The "!30m" is read, but the capture is refused whole for the repeat, so promising a
        // reminder would describe a task that isn't created — the same reason the due date goes
        // unsaid for a recurrence.
        var preview = Preview("task !30m every day");

        Assert.DoesNotContain("remind", preview);
    }
}
