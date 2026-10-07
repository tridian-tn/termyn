using System.Globalization;
using Termyn.Core.Capture;

namespace Termyn.Presentation;

/// <summary>
/// What a date box says under itself about what's been typed into it, and whether OK will take it.
/// </summary>
/// <param name="Says">The line under the box</param>
/// <param name="Accepted">Whether the box's dialog can be closed with it</param>
public sealed record DayVerdict(string Says, bool Accepted);

/// <summary>
/// How the due-date box and the deadline box describe what they've read, and how the reminders
/// dialog says why it won't take a moment.
/// </summary>
/// <remarks>
/// The two read with the same grammar and differ only in what they'll take. A due date takes
/// anything, since what isn't read here goes to Todoist as the words and a repeat can only be
/// written that way. A deadline is a day and nothing else — no time, no schedule, and no words left
/// for a server that has no field to read them into — so the box says what it can't take, and OK
/// waits for something it can.
///
/// Here rather than in the dialogs, because none of it needs a window and all of it is wording.
///
/// Every example offered is one the box reads, so for an account that reads its dates in another
/// language they're in figures: the English words would be refused by the box offering them.
/// </remarks>
public static class DayBoxText
{
    /// <summary>What an empty box suggests typing, every one of which the box reads.</summary>
    /// <param name="english">Whether the account reads a typed date in English</param>
    /// <returns>The examples, between commas</returns>
    public static string Hint(bool english) => english ? "today, friday, 4 aug, in 3 days" : "25/12, 2026-12-25";

    /// <summary>What the box for a reminder's moment suggests typing while it's empty.</summary>
    /// <param name="english">Whether the account reads a typed date in English</param>
    /// <returns>The example</returns>
    public static string ReminderHint(bool english) => english ? "2026-08-03 9am" : "2026-08-03 09:00";

    /// <summary>What the due-date box says about what's been typed into it.</summary>
    /// <param name="reading">What it was read as</param>
    /// <returns>The line under the box, and whether OK takes it</returns>
    public static DayVerdict ForDue(DayReading reading) => reading.Kind switch
    {
        DayReadingKind.Blank => new("No due date", true),
        DayReadingKind.Day => new(Written(reading), true),
        DayReadingKind.Repeat => new("Repeats — Todoist works out the dates when it syncs", true),
        _ => new("Todoist reads this when it syncs", true),
    };

    /// <summary>What the deadline box says about what's been typed into it.</summary>
    /// <param name="reading">What it was read as</param>
    /// <param name="english">Whether the account reads a typed date in English</param>
    /// <returns>The line under the box, and whether OK takes it</returns>
    public static DayVerdict ForDeadline(DayReading reading, bool english) => reading.Kind switch
    {
        DayReadingKind.Blank => new("No deadline", true),
        DayReadingKind.Day when reading.Time is null => new(Written(reading), true),
        DayReadingKind.Day => new("A deadline is a whole day, without a time", false),
        DayReadingKind.Repeat => new("A deadline can't repeat", false),
        _ when english => new("Not a day this can read — try “4 aug” or “in 3 days”", false),
        _ => new("Not a day this can read — try “25/12” or “2026-12-25”", false),
    };

    /// <summary>
    /// What the reminders dialog says about the moment typed for a reminder, and whether it's added.
    /// </summary>
    /// <remarks>
    /// Read as strictly as the date boxes read: a reminder is a moment, set once, and the API takes
    /// a date and time and nothing else. So a repeat is refused by name, and words left over mean
    /// the moment wasn't read — rather than taking the day and quietly dropping the rest, as reading
    /// it like a captured task's title did. A day with no time on it is at nine.
    /// </remarks>
    /// <param name="reading">What was typed, read as a day</param>
    /// <param name="english">Whether the account reads a typed date in English</param>
    /// <returns>Why it's refused, or the moment it's for when it isn't</returns>
    public static DayVerdict ForReminder(DayReading reading, bool english) => reading.Kind switch
    {
        DayReadingKind.Day => new(Written(reading with { Time = reading.Time ?? ReminderTime }), true),
        DayReadingKind.Repeat => new("A reminder can't repeat", false),
        DayReadingKind.Unread when english => new("Not a day and time this can read — try “tomorrow 9am”", false),
        DayReadingKind.Unread => new("Not a day and time this can read — try “25/12 09:00”", false),
        _ => new(string.Empty, false),
    };

    /// <summary>When a reminder set for a day with no time on it goes off.</summary>
    public static TimeOnly ReminderTime { get; } = new(9, 0);

    /// <summary>
    /// A day read from the box, written out in full so there's no mistaking which one it is.
    /// </summary>
    /// <remarks>
    /// In the machine's own language and calendar, as a date shown to somebody should be — unlike
    /// the box, whose words have to be ones the grammar reads back. Some calendars cover only a
    /// stretch of years, the Umm al-Qura one Saudi Arabia uses among them, and throw for a day
    /// outside it; that day is written the invariant way instead, since the box reads on every
    /// keystroke and an old deadline would otherwise stop its dialog opening at all.
    /// </remarks>
    /// <param name="reading">A reading of a day</param>
    /// <returns>The day, with its time when it has one</returns>
    private static string Written(DayReading reading)
    {
        var day = reading.Day!.Value;
        string written;

        try
        {
            written = day.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            written = day.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
        }

        return reading.Time is { } time ? $"{written} at {time.ToString("t", CultureInfo.CurrentCulture)}" : written;
    }
}
