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
    public static DateOnly? DueOn(TaskItem item, TimeZoneInfo zone) => DayOf(item.DueDate, zone);

    /// <summary>
    /// The day a task was added, in the account's timezone.
    /// </summary>
    /// <remarks>
    /// Always an instant, unlike a due date: a task is created at a moment, and Todoist writes that
    /// in UTC. So this is the case the conversion exists for — a task added late in the evening
    /// west of UTC was added yesterday as far as the server is concerned and today as far as the
    /// person who added it is.
    /// </remarks>
    public static DateOnly? AddedOn(TaskItem item, TimeZoneInfo zone) => DayOf(item.AddedAt, zone);

    /// <summary>
    /// The day a task has to be finished by.
    /// </summary>
    /// <remarks>
    /// The zone is taken and ignored, which is the honest way to say that a deadline needs none: it
    /// is written as a plain calendar date and means the same day wherever it is read. Asking for it
    /// the same way as the other two keeps the callers from having to know that.
    /// </remarks>
    public static DateOnly? DeadlineOn(TaskItem item, TimeZoneInfo zone) => DayOf(item.Deadline, zone);

    /// <summary>
    /// Whether a task's due date names an hour as well as a day.
    /// </summary>
    /// <remarks>
    /// Todoist writes a whole-day date as the date alone and anything with a time as a datetime, so
    /// the separator is the whole of the question. A task with no due date has no time of day
    /// either, and this says so.
    /// </remarks>
    public static bool DueHasTime(TaskItem item) => item.DueDate is { } due && due.Contains('T');

    /// <summary>
    /// The day a Todoist timestamp falls on in the account's timezone.
    /// </summary>
    /// <remarks>
    /// Todoist sends a floating date or a local datetime as-is, but anything with a fixed timezone
    /// arrives as a UTC instant — taking the date off the front of that would be a day out either
    /// side of midnight.
    /// </remarks>
    /// <param name="stamp">The date or instant as the server wrote it, or null</param>
    /// <param name="zone">The account's timezone</param>
    /// <returns>The day, or null when there is nothing readable there</returns>
    private static DateOnly? DayOf(string? stamp, TimeZoneInfo zone)
    {
        if (stamp is null || stamp.Length < 10)
            return null;

        if (stamp.EndsWith('Z')
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        }

        return DateOnly.TryParseExact(stamp[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
