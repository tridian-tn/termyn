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

    [Theory]
    [InlineData("2026-07-31", null, "2026-07-31T09:00:00Z", true)]   // due today
    [InlineData("2026-07-31", null, "2026-07-29T09:00:00Z", true)]   // due today, done early
    [InlineData("2026-07-28", null, "2026-07-31T09:00:00Z", false)]  // overdue, done today: Todoist drops it once it's done
    [InlineData("2026-05-02", null, "2026-05-09T09:00:00Z", false)]  // overdue, done months ago
    [InlineData("2026-08-01", null, "2026-07-31T09:00:00Z", false)]  // due tomorrow, done early
    [InlineData(null, null, "2026-07-31T09:00:00Z", false)]          // no date at all
    [InlineData(null, "2026-07-31", "2026-07-30T09:00:00Z", false)]  // a deadline today doesn't place it, as it wouldn't an open task
    public void Today_takes_completed_tasks_due_today_whenever_they_were_done(string? due, string? deadline, string completedAt, bool expected)
        => Assert.Equal(expected, SmartViews.MatchesCompleted(Completed(due, completedAt, deadline: deadline), SmartView.Today, Today, Utc, null));

    [Fact]
    public void A_completed_tasks_due_date_is_read_in_the_accounts_zone()
    {
        // 23:00 UTC on the 31st is already the 1st in Auckland, and still the 31st in New York.
        var item = Completed("2026-07-31T23:00:00.000000Z", "2026-07-30T09:00:00Z");
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.False(SmartViews.MatchesCompleted(item, SmartView.Today, Today, auckland, null));
        Assert.True(SmartViews.MatchesCompleted(item, SmartView.Today, Today, newYork, null));
    }

    [Fact]
    public void Completed_tasks_match_the_other_views_as_active_ones_do()
    {
        Assert.True(SmartViews.MatchesCompleted(Completed("2026-08-01", "2026-07-20T09:00:00Z"), SmartView.Upcoming, Today, Utc, null));
        Assert.False(SmartViews.MatchesCompleted(Completed(null, "2026-07-20T09:00:00Z", deadline: "2026-08-03"), SmartView.Upcoming, Today, Utc, null));
        Assert.True(SmartViews.MatchesCompleted(Completed(null, "2026-07-20T09:00:00Z"), SmartView.All, Today, Utc, null));
        Assert.False(SmartViews.MatchesCompleted(Completed("2026-07-31", null, projectId: "work"), SmartView.Inbox, Today, Utc, "inbox"));
    }

    private static TaskItem Completed(string? due, string? completedAt, string? projectId = null, string? deadline = null)
    {
        var fields = new List<string> { "\"id\":\"i\"", "\"content\":\"T\"", "\"checked\":true" };
        if (due is not null)
            fields.Add($"\"due\":{{\"date\":\"{due}\"}}");
        if (deadline is not null)
            fields.Add($"\"deadline\":{{\"date\":\"{deadline}\"}}");
        if (completedAt is not null)
            fields.Add($"\"completed_at\":\"{completedAt}\"");
        if (projectId is not null)
            fields.Add($"\"project_id\":\"{projectId}\"");
        return Projections.ToTaskItem(Json.Object("{" + string.Join(",", fields) + "}"));
    }

    private static TaskItem Item(string? due, string? projectId = null)
        => Projections.ToTaskItem(Json.Object(due is null
            ? $$"""{"id":"i","content":"T"{{(projectId is null ? "" : $",\"project_id\":\"{projectId}\"")}}}"""
            : $$"""{"id":"i","content":"T","due":{"date":"{{due}}"}{{(projectId is null ? "" : $",\"project_id\":\"{projectId}\"")}}}"""));
}
