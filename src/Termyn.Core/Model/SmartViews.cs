using System.Globalization;

namespace Termyn.Core.Model;

/// <summary>The built-in views in the sidebar, alongside projects.</summary>
public enum SmartView
{
    Today,
    Upcoming,
    Inbox,
    All,
}

/// <summary>
/// The predicates behind the built-in views. Dates are compared against "today" in the account's
/// own timezone, which the caller resolves — the machine's local date can be a day out.
/// </summary>
public static class SmartViews
{
    /// <summary>How far ahead <see cref="SmartView.Upcoming"/> looks, beyond today.</summary>
    public const int UpcomingDays = 7;

    /// <summary>Due today or earlier: overdue tasks belong here rather than being hidden.</summary>
    public static bool IsToday(TaskItem item, DateOnly today, TimeZoneInfo zone)
        => DueOn(item, zone) is { } due && due <= today;

    /// <summary>Due within the next week, not counting today.</summary>
    public static bool IsUpcoming(TaskItem item, DateOnly today, TimeZoneInfo zone)
        => DueOn(item, zone) is { } due && due > today && due <= today.AddDays(UpcomingDays);

    public static bool IsInbox(TaskItem item, string? inboxProjectId)
        => item.ProjectId is null || item.ProjectId == inboxProjectId;

    public static bool Matches(TaskItem item, SmartView view, DateOnly today, TimeZoneInfo zone, string? inboxProjectId) => view switch
    {
        SmartView.Today => IsToday(item, today, zone),
        SmartView.Upcoming => IsUpcoming(item, today, zone),
        SmartView.Inbox => IsInbox(item, inboxProjectId),
        _ => true,
    };

    /// <summary>
    /// The day a task falls due in the account's timezone. Todoist sends a floating date or local
    /// datetime as-is, but a task with a fixed timezone arrives as a UTC instant — taking the date
    /// off the front of that would be a day out either side of midnight.
    /// </summary>
    public static DateOnly? DueOn(TaskItem item, TimeZoneInfo zone)
    {
        var due = item.DueDate;
        if (due is null || due.Length < 10)
            return null;

        if (due.EndsWith('Z')
            && DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        }

        return DateOnly.TryParseExact(due[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
