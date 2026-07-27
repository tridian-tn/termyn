using Termyn.Core.Model;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

public class SmartViewsTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Theory]
    [InlineData("2026-07-31", true)]   // today
    [InlineData("2026-07-30", true)]   // overdue belongs in Today, not hidden
    [InlineData("2026-01-01", true)]
    [InlineData("2026-08-01", false)]  // tomorrow
    [InlineData(null, false)]          // no due date
    public void Today_includes_overdue_but_not_the_future(string? due, bool expected)
        => Assert.Equal(expected, SmartViews.IsToday(Item(due), Today));

    [Theory]
    [InlineData("2026-08-01", true)]   // tomorrow
    [InlineData("2026-08-07", true)]   // the last day of the window
    [InlineData("2026-08-08", false)]  // just beyond it
    [InlineData("2026-07-31", false)]  // today belongs to Today
    [InlineData("2026-07-30", false)]  // overdue does too
    [InlineData(null, false)]
    public void Upcoming_covers_the_next_week_after_today(string? due, bool expected)
        => Assert.Equal(expected, SmartViews.IsUpcoming(Item(due), Today));

    [Fact]
    public void A_timestamped_due_date_is_read_by_its_day()
    {
        Assert.True(SmartViews.IsToday(Item("2026-07-31T16:00:00"), Today));
        Assert.True(SmartViews.IsUpcoming(Item("2026-08-01T09:30:00Z"), Today));
    }

    [Fact]
    public void An_unreadable_due_date_is_treated_as_none()
    {
        Assert.Null(SmartViews.DueOn(Item("not a date")));
        Assert.Null(SmartViews.DueOn(Item("2026-13-45")));
        Assert.False(SmartViews.IsToday(Item("soon"), Today));
    }

    [Fact]
    public void Inbox_holds_tasks_in_it_and_tasks_with_no_project()
    {
        Assert.True(SmartViews.IsInbox(Item(null, projectId: "inbox"), "inbox"));
        Assert.True(SmartViews.IsInbox(Item(null, projectId: null), "inbox"));
        Assert.False(SmartViews.IsInbox(Item(null, projectId: "work"), "inbox"));
    }

    [Fact]
    public void All_matches_everything()
        => Assert.True(SmartViews.Matches(Item(null, projectId: "work"), SmartView.All, Today, "inbox"));

    private static TaskItem Item(string? due, string? projectId = null)
        => Projections.ToTaskItem(Json.Object(due is null
            ? $$"""{"id":"i","content":"T"{{(projectId is null ? "" : $",\"project_id\":\"{projectId}\"")}}}"""
            : $$"""{"id":"i","content":"T","due":{"date":"{{due}}"}{{(projectId is null ? "" : $",\"project_id\":\"{projectId}\"")}}}"""));
}
