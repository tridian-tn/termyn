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
}
