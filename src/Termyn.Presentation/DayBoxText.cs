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
/// How the due-date box and the deadline box describe what they've read.
/// </summary>
/// <remarks>
/// The two read with the same grammar and differ only in what they'll take. A due date takes
/// anything, since what isn't read here goes to Todoist as the words and a repeat can only be
/// written that way. A deadline is a day and nothing else — no time, no schedule, and no words left
/// for a server that has no field to read them into — so the box says what it can't take, and OK
/// waits for something it can.
///
/// Here rather than in the dialogs, because none of it needs a window and all of it is wording.
/// </remarks>
public static class DayBoxText
{
    /// <summary>What an empty box suggests typing, every one of which the box reads.</summary>
    public const string Hint = "today, friday, 4 aug, in 3 days";

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
    /// <returns>The line under the box, and whether OK takes it</returns>
    public static DayVerdict ForDeadline(DayReading reading) => reading.Kind switch
    {
        DayReadingKind.Blank => new("No deadline", true),
        DayReadingKind.Day when reading.Time is null => new(Written(reading), true),
        DayReadingKind.Day => new("A deadline is a whole day, without a time", false),
        DayReadingKind.Repeat => new("A deadline can't repeat", false),
        _ => new("Not a day this can read — try “4 aug” or “in 3 days”", false),
    };

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
