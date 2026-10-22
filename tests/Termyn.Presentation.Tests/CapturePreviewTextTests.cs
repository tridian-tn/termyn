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

    private static string Preview(string text)
        => CapturePreviewText.For(new CapturePreview(Parse(text), ProjectResolved: true, SectionResolved: true));

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
    public void An_unknown_project_says_where_the_task_will_go_instead()
    {
        var preview = CapturePreviewText.For(
            new CapturePreview(Parse("call mum #Nowhere"), ProjectResolved: false, SectionResolved: true));

        Assert.Contains("goes to Inbox", preview);
    }

    [Fact]
    public void The_empty_hint_reads_what_the_catalogue_says()
    {
        Assert.Equal("Add a task…  #project /section @label p1 tomorrow 4pm", CapturePreviewText.Hint(english: true));
        Assert.Equal("Add a task…  #project /section @label p1", CapturePreviewText.Hint(english: false));
    }

    [Fact]
    public void An_unknown_section_says_it_is_unknown()
    {
        var parse = Parse("call mum") with { SectionName = "Nowhere" };
        var preview = CapturePreviewText.For(new CapturePreview(parse, ProjectResolved: true, SectionResolved: false));

        Assert.Contains("/Nowhere (unknown)", preview);
    }

    [Fact]
    public void A_field_that_needs_the_server_is_named_as_offline()
    {
        var parse = Parse("call mum") with { Unsupported = ["deadline", "assignee"] };
        var preview = CapturePreviewText.For(new CapturePreview(parse, ProjectResolved: true, SectionResolved: true));

        Assert.Contains("(needs a connection: deadline, assignee)", preview);
    }
}
