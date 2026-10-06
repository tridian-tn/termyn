namespace Termyn.Core.Capture;

/// <summary>What a box asking for a day was given.</summary>
public enum DayReadingKind
{
    /// <summary>Nothing at all, which asks for the date to be cleared.</summary>
    Blank,

    /// <summary>A day, and perhaps a time on it, read here.</summary>
    Day,

    /// <summary>A repeating schedule, which only the server can work out.</summary>
    Repeat,

    /// <summary>Words the grammar here doesn't read, which only the server might.</summary>
    Unread,
}

/// <summary>
/// What the whole of a date box's text was read as.
/// </summary>
/// <param name="Kind">Which kind of answer it is</param>
/// <param name="Day">The day, when it read as one</param>
/// <param name="Time">The time of day on it, when one was given</param>
public sealed record DayReading(DayReadingKind Kind, DateOnly? Day = null, TimeOnly? Time = null)
{
    public static DayReading Blank { get; } = new(DayReadingKind.Blank);

    public static DayReading Repeat { get; } = new(DayReadingKind.Repeat);

    public static DayReading Unread { get; } = new(DayReadingKind.Unread);

    /// <summary>A day, read here.</summary>
    /// <param name="day">The day</param>
    /// <param name="time">The time of day on it, or null for the whole day</param>
    /// <returns>The reading</returns>
    public static DayReading On(DateOnly day, TimeOnly? time = null) => new(DayReadingKind.Day, day, time);
}
