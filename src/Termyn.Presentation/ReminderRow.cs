using System.Globalization;
using Termyn.Core.Model;

namespace Termyn.Presentation;

/// <summary>
/// A reminder on a task, as the list of them says it in words.
/// </summary>
/// <remarks>
/// A list shows these by <see cref="ToString"/>, so the wording is here, where it can be tested,
/// rather than in the dialog that lists them.
/// </remarks>
/// <param name="Reminder">The reminder it stands for</param>
public sealed record ReminderRow(Reminder Reminder)
{
    /// <summary>
    /// Whether Termyn could put this reminder back if it were removed. A kind it can't author is a
    /// one-way door, so it isn't offered.
    /// </summary>
    public bool CanRemove => Reminder.Kind is ReminderKind.Relative or ReminderKind.Absolute;

    public override string ToString() => Reminder.Kind switch
    {
        ReminderKind.Absolute when Reminder.Schedule is { } schedule => Repeating(schedule, Reminder.DueDate),
        ReminderKind.Absolute when Reminder.DueDate is { } due => string.Format(CultureInfo.CurrentCulture, Strings.ReminderAt, Moment(due)),
        ReminderKind.Absolute => Strings.ReminderAtUnknownTime,
        ReminderKind.Location when Reminder.LocationName is { } place => string.Format(CultureInfo.CurrentCulture, Strings.ReminderAtPlace, place),
        ReminderKind.Location => Strings.ReminderAtUnnamedPlace,
        ReminderKind.Unknown => Strings.ReminderSetElsewhere,
        _ => Reminder.MinuteOffset == 0
            ? Strings.ReminderWhenDue
            : Before(Reminder.MinuteOffset),
    };

    /// <summary>
    /// An absolute reminder's moment, in words rather than the timestamp the server sent, so it
    /// sits beside the relative ones instead of standing out as raw data. A timestamp that can't be
    /// read is shown as the server wrote it.
    /// </summary>
    private static string Moment(string due)
        => DateTime.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? DisplayDates.WeekdayAndTime(when, CultureInfo.CurrentCulture)
            : due;

    /// <summary>
    /// A reminder that repeats, by its schedule, and by when it next fires once the server has said.
    /// One just added offline has no next time yet, and the schedule alone is all there is to show.
    /// </summary>
    /// <param name="schedule">The words it repeats on, such as "every sat 9am"</param>
    /// <param name="due">The next time it fires, as the server wrote it, or null before it has</param>
    /// <returns>The words, such as "Every sat 9am (next Sat, 24 Oct 09:00)"</returns>
    private static string Repeating(string schedule, string? due)
    {
        var words = schedule.Length > 0 ? char.ToUpperInvariant(schedule[0]) + schedule[1..] : schedule;
        return due is null ? words : string.Format(CultureInfo.CurrentCulture, Strings.ReminderRepeating, words, Moment(due));
    }

    /// <summary>
    /// Offsets aren't limited to the ones the dialog offers — the web app sets whatever it likes —
    /// so the odd sizes have to read properly too.
    /// </summary>
    private static string Before(int minutes) => minutes switch
    {
        < 60 => Counted(nameof(Strings.ReminderMinutesBefore_Other), minutes),
        < 1440 when minutes % 60 == 0 => Counted(nameof(Strings.ReminderHoursBefore_Other), minutes / 60),
        _ when minutes % 1440 == 0 => Counted(nameof(Strings.ReminderDaysBefore_Other), minutes / 1440),
        _ => Counted(nameof(Strings.ReminderMinutesBefore_Other), minutes),
    };

    private static string Counted(string otherName, int count) => Plurals.Format(Strings.ResourceManager, otherName, count);
}
