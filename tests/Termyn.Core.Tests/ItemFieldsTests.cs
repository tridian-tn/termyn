using Termyn.Core.Capture;
using Termyn.Core.Model;

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
            Labels = ["followup"],
            Priority = Priority.P1,
            DueDate = new DateOnly(2026, 7, 31),
            DueTime = new TimeOnly(16, 0),
        };

        var fields = ItemFields.ForAdd(parse, name => name == "Work" ? "p1" : null);

        Assert.Equal("Email report", fields["content"]!.ToString());
        Assert.Equal("p1", fields["project_id"]!.ToString());
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
    public void An_unknown_project_name_is_left_unset_so_the_task_lands_in_the_inbox()
    {
        var parse = new QuickAddParse { Content = "Task", ProjectName = "Nope" };

        var fields = ItemFields.ForAdd(parse, _ => null);

        Assert.False(fields.ContainsKey("project_id"));
    }

    [Fact]
    public void A_date_without_a_time_stays_all_day()
        => Assert.Equal("2026-07-31", ItemFields.Due(new DateOnly(2026, 7, 31), null)!["date"]!.ToString());

    [Fact]
    public void No_date_means_no_due_object()
        => Assert.Null(ItemFields.Due(null, new TimeOnly(9, 0)));
}
