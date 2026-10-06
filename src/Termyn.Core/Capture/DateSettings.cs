namespace Termyn.Core.Capture;

/// <summary>
/// What an account says about reading a typed date: where it is, and the settings Todoist reads
/// one by.
/// </summary>
/// <remarks>
/// Todoist reads "5/6", "next friday" and "next week" by the account's own settings, so reading them
/// any other way here would make the same words mean one day offline and another once the server
/// had them. A setting the account hasn't given is null, and the forms that turn on it aren't read
/// at all — they're left for the server, which knows, rather than guessed at here.
/// </remarks>
/// <param name="TimeZone">The account's timezone, which "today" is read in</param>
/// <param name="DayFirst">Whether a date in figures puts the day before the month, or null when it isn't known</param>
/// <param name="WeekStart">The day the account's week starts on, or null when it isn't known</param>
/// <param name="NextWeek">The day the account calls "next week", or null when it isn't known</param>
public sealed record DateSettings(
    TimeZoneInfo TimeZone,
    bool? DayFirst = null,
    DayOfWeek? WeekStart = null,
    DayOfWeek? NextWeek = null)
{
    /// <summary>Nothing known about the account: the machine's own timezone, and none of its settings.</summary>
    public static DateSettings Unknown => new(TimeZoneInfo.Local);
}
