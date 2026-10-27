using System.Text.Json.Nodes;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

public class ItemFieldsTests
{
    [Fact]
    public void Builds_the_full_field_set_from_a_parse()
    {
        var parse = new QuickAddParse
        {
            Content = "Email report",
            ProjectName = "Work",
            SectionName = "Reports",
            Labels = ["followup"],
            Priority = Priority.P1,
            DueDate = new DateOnly(2026, 7, 31),
            DueTime = new TimeOnly(16, 0),
        };

        var fields = ItemFields.ForAdd(parse, projectId: "p1", sectionId: "s1");

        Assert.Equal("Email report", fields["content"]!.ToString());
        Assert.Equal("p1", fields["project_id"]!.ToString());
        Assert.Equal("s1", fields["section_id"]!.ToString());
        Assert.Equal("followup", fields["labels"]!.AsArray().Single()!.ToString());
        Assert.Equal("4", fields["priority"]!.ToString()); // UI P1 -> API 4
        Assert.Equal("2026-07-31T16:00:00", fields["due"]!["date"]!.ToString());
    }

    [Fact]
    public void Omits_everything_that_was_not_given()
    {
        var fields = ItemFields.ForAdd(new QuickAddParse { Content = "Buy milk" });

        Assert.Equal(new[] { "content" }, fields.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public void An_unresolved_project_is_left_unset_so_the_task_lands_in_the_inbox()
    {
        var parse = new QuickAddParse { Content = "Task", ProjectName = "Nope" };

        var fields = ItemFields.ForAdd(parse, projectId: null);

        Assert.False(fields.ContainsKey("project_id"));
    }

    [Fact]
    public void A_date_without_a_time_stays_all_day()
        => Assert.Equal("2026-07-31", ItemFields.Due(new DateOnly(2026, 7, 31), null)!["date"]!.ToString());

    [Fact]
    public void No_date_means_no_due_object()
        => Assert.Null(ItemFields.Due(null, new TimeOnly(9, 0)));

    [Fact]
    public void A_deadline_is_sent_as_the_day_alone()
    {
        var deadline = ItemFields.Deadline(new DateOnly(2026, 8, 4))!;

        Assert.Equal("2026-08-04", deadline["date"]!.ToString());

        // Todoist tags what it read back with the language it read it in. Sending one would be
        // claiming words were read where a date was picked.
        Assert.Equal(["date"], deadline.Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public void No_day_clears_the_deadline()
        => Assert.Null(ItemFields.Deadline(null));

    [Fact]
    public void Recreating_a_task_keeps_only_the_fields_a_client_may_send()
    {
        var prior = Json.Object("""
        {
          "id": "i1", "content": "A", "priority": 3, "project_id": "p1", "due": {"date":"2026-07-31"},
          "user_id": "u1", "added_at": "2026-01-01T00:00:00Z", "v2_id": "xyz", "is_deleted": false, "checked": true
        }
        """);

        var fields = ItemFields.ForRecreate(prior);

        Assert.Equal(new[] { "content", "due", "priority", "project_id" }, fields.Select(kv => kv.Key).OrderBy(k => k).ToArray());
        Assert.Equal("A", fields["content"]!.ToString());
    }

    // ---- Rescheduling ----------------------------------------------------------------------------

    private static readonly DateOnly Moved = new(2026, 11, 2);

    private static JsonObject Rescheduled(string? due, string zone = "UTC")
        => ItemFields.Rescheduled(
            due is null ? null : JsonNode.Parse(due)!.AsObject(),
            Moved,
            TimeZoneInfo.FindSystemTimeZoneById(zone));

    [Fact]
    public void A_task_with_no_due_date_is_moved_onto_the_whole_day()
        => Assert.Equal("""{"date":"2026-11-02"}""", Rescheduled(null).ToJsonString());

    [Fact]
    public void A_whole_day_stays_a_whole_day()
        => Assert.Equal("""{"date":"2026-11-02"}""", Rescheduled("""{"date":"2026-10-27","string":"tomorrow","is_recurring":false}""").ToJsonString());

    [Fact]
    public void A_time_goes_with_the_task_to_its_new_day()
        => Assert.Equal("2026-11-02T16:30:00", Rescheduled("""{"date":"2026-10-27T16:30:00","is_recurring":false}""")["date"]!.ToString());

    [Fact]
    public void A_one_off_goes_without_the_words_it_was_set_with()
    {
        // "tomorrow" sent back today would have the server read a different day from the one asked for.
        var moved = Rescheduled("""{"date":"2026-10-27","string":"tomorrow","lang":"en","is_recurring":false}""");

        Assert.Null(moved["string"]);
        Assert.Null(moved["lang"]);
    }

    [Fact]
    public void A_repeat_keeps_its_words_so_it_goes_on_repeating()
    {
        var moved = Rescheduled("""{"date":"2026-10-26T09:00:00","string":"every monday 9am","lang":"en","is_recurring":true}""");

        Assert.Equal("2026-11-02T09:00:00", moved["date"]!.ToString());
        Assert.Equal("every monday 9am", moved["string"]!.ToString());
        Assert.Equal("en", moved["lang"]!.ToString());
        Assert.True(moved["is_recurring"]!.GetValue<bool>());
    }

    [Fact]
    public void A_fixed_time_is_kept_in_its_own_zone_across_a_clock_change()
    {
        // Nine in London is 08:00Z in summer and 09:00Z in winter, and the clocks go back between
        // the two days. Keeping the UTC time would move the task to ten.
        var moved = Rescheduled("""{"date":"2026-10-24T08:00:00Z","timezone":"Europe/London","is_recurring":false}""", zone: "Pacific/Auckland");

        Assert.Equal("2026-11-02T09:00:00Z", moved["date"]!.ToString());
        Assert.Equal("Europe/London", moved["timezone"]!.ToString());
    }

    [Fact]
    public void A_fixed_time_whose_zone_is_unknown_is_kept_in_the_accounts()
    {
        var moved = Rescheduled("""{"date":"2026-10-24T08:00:00Z","timezone":"Nowhere/Special","is_recurring":false}""", zone: "Europe/London");

        Assert.Equal("2026-11-02T09:00:00Z", moved["date"]!.ToString());
    }
}
