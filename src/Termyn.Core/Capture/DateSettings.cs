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
/// <param name="Language">The language the account reads a typed date in, as Todoist names it — <c>en</c>, <c>de</c>, <c>pt_BR</c> — or null when it isn't known</param>
public sealed record DateSettings(
    TimeZoneInfo TimeZone,
    bool? DayFirst = null,
    DayOfWeek? WeekStart = null,
    DayOfWeek? NextWeek = null,
    string? Language = null)
{
    /// <summary>Nothing known about the account: the machine's own timezone, and none of its settings.</summary>
    public static DateSettings Unknown => new(TimeZoneInfo.Local);

    /// <summary>
    /// Whether the account reads a typed date in English, the only language there are words for here.
    /// </summary>
    /// <remarks>
    /// An account that reads another one reads "tomorrow" as a word of its own language or not at
    /// all, so for it a box asking for a day reads only figures — a date like 25/12 or 2026-12-25,
    /// and a time like 16:30 — and a capture reads no day at all. Every word is left for the server.
    ///
    /// The one setting where not knowing doesn't mean not reading. Todoist reads English until an
    /// account says otherwise, and the language is on every account, so it's only unknown before the
    /// first sync — and refusing every word until then would cost a first capture its day for the
    /// sake of the few accounts that read another language.
    /// </remarks>
    public bool ReadsEnglish
        => string.IsNullOrWhiteSpace(Language)
           || Language.Equals("en", StringComparison.OrdinalIgnoreCase)
           || Language.StartsWith("en_", StringComparison.OrdinalIgnoreCase)
           || Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
}
