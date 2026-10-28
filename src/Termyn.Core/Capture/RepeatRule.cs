namespace Termyn.Core.Capture;

/// <summary>How far apart a repeat's occurrences are counted.</summary>
public enum RepeatUnit
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>
/// A repeating due date in parts, as the due-date dialog's Repeat list and its Custom dialog build
/// one.
/// </summary>
/// <remarks>
/// <para>
/// Never sent as it is. Todoist has no field that says "repeat weekly", only the words, so
/// <see cref="RepeatWords"/> writes this out as the words that go and reads the same words back.
/// </para>
/// <para>
/// It holds what Todoist's own Custom dialog does: every so many days, weeks, months or years,
/// counted from the due date or from the day it's ticked off, starting on a day and running until
/// another or for ever, at a time or on the whole day. A week can name its days instead — "every
/// mon, fri" — and a month or a year repeats on the day it starts on.
/// </para>
/// </remarks>
/// <param name="Unit">What the repeat counts in</param>
/// <param name="Interval">How many of them apart, one or more</param>
/// <param name="Weekdays">
/// The days of the week it falls on, for a weekly repeat counted from the due date. Empty or null
/// for any other, which falls on the day it starts and every interval on from it
/// </param>
/// <param name="Start">The first day it falls on, or null for Todoist's own first day</param>
/// <param name="Until">The last day it can fall on, or null when it never stops</param>
/// <param name="Time">The time it's due at, or null for the whole day</param>
/// <param name="FromCompletion">
/// Whether the next one is counted from the day it's ticked off rather than from the day it was
/// due — Todoist's <c>every!</c>
/// </param>
public sealed record RepeatRule(
    RepeatUnit Unit,
    int Interval = 1,
    IReadOnlyList<DayOfWeek>? Weekdays = null,
    DateOnly? Start = null,
    DateOnly? Until = null,
    TimeOnly? Time = null,
    bool FromCompletion = false)
{
    /// <summary>The days of the week it names, which is none when it names none.</summary>
    public IReadOnlyList<DayOfWeek> Days => Weekdays ?? [];

    /// <summary>
    /// Whether it falls on named days of the week. Only a repeat every single week, counted from
    /// the due date, can say that in Todoist's words; any other falls on the day it starts.
    /// </summary>
    public bool OnWeekdays => Unit is RepeatUnit.Week && Interval == 1 && !FromCompletion && Days.Count > 0;
}
