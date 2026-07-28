using Termyn.Core.Model;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

public class SmartViewsTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("2026-07-31", true)]   // today
    [InlineData("2026-07-30", true)]   // overdue belongs in Today, not hidden
    [InlineData("2026-01-01", true)]
    [InlineData("2026-08-01", false)]  // tomorrow
    [InlineData(null, false)]          // no due date
    public void Today_includes_overdue_but_not_the_future(string? due, bool expected)
        => Assert.Equal(expected, SmartViews.IsToday(Item(due), Today, Utc));

    [Theory]
    [InlineData("2026-08-01", true)]   // tomorrow
    [InlineData("2026-08-07", true)]   // the last day of the window
    [InlineData("2026-08-08", false)]  // just beyond it
    [InlineData("2026-07-31", false)]  // today belongs to Today
    [InlineData("2026-07-30", false)]  // overdue does too
    [InlineData(null, false)]
    public void Upcoming_covers_the_next_week_after_today(string? due, bool expected)
        => Assert.Equal(expected, SmartViews.IsUpcoming(Item(due), Today, Utc));

    [Fact]
    public void A_local_datetime_is_read_by_its_day()
    {
        Assert.True(SmartViews.IsToday(Item("2026-07-31T16:00:00"), Today, Utc));
        Assert.True(SmartViews.IsUpcoming(Item("2026-08-01T09:30:00"), Today, Utc));
    }

    [Fact]
    public void A_fixed_timezone_due_date_is_read_in_the_accounts_zone()
    {
        // 23:00 UTC on the 31st is already the 1st in Auckland (UTC+12), and still the 31st in
        // New York (UTC-4). Taking the date off the front of the string would say "the 31st" for both.
        var lateUtc = Item("2026-07-31T23:00:00.000000Z");

        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.Equal(new DateOnly(2026, 8, 1), SmartViews.DueOn(lateUtc, auckland));
        Assert.Equal(new DateOnly(2026, 7, 31), SmartViews.DueOn(lateUtc, newYork));

        Assert.False(SmartViews.IsToday(lateUtc, Today, auckland)); // it's tomorrow there
        Assert.True(SmartViews.IsToday(lateUtc, Today, newYork));
    }

    [Fact]
    public void An_unreadable_due_date_is_treated_as_none()
    {
        Assert.Null(SmartViews.DueOn(Item("not a date"), Utc));
        Assert.Null(SmartViews.DueOn(Item("2026-13-45"), Utc));
        Assert.False(SmartViews.IsToday(Item("soon"), Today, Utc));
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
        => Assert.True(SmartViews.Matches(Item(null, projectId: "work"), SmartView.All, Today, Utc, "inbox"));

    private static TaskItem Item(string? due, string? projectId = null)
        => Projections.ToTaskItem(Json.Object(due is null
            ? $$"""{"id":"i","content":"T"{{(projectId is null ? "" : $",\"project_id\":\"{projectId}\"")}}}"""
            : $$"""{"id":"i","content":"T","due":{"date":"{{due}}"}{{(projectId is null ? "" : $",\"project_id\":\"{projectId}\"")}}}"""));
}
