using System.Globalization;

namespace Termyn.Presentation;

/// <summary>
/// Dates as Termyn writes them for somebody to read, arranged the way the machine's region
/// arranges them.
/// </summary>
/// <remarks>
/// Built from the region's own patterns rather than written out here, so the order, punctuation and
/// calendar are the region's — "5 Oct" in Britain, "Oct 5" in the States, "10月5日" in Japan — along
/// with anything changed in Windows' region settings. Nothing here is for the server: a date
/// Todoist reads, or wording reproduced the way it writes it, stays invariant where it's written.
///
/// A region has a long date and a month-and-day, but no brief one, so the brief forms are derived:
/// month names abbreviated and the day unpadded, which is how a region writes a date in brief even
/// where its long date says "05 October". A weekday goes where the region's long date puts it,
/// with the same punctuation, and a region whose long date has none gets none. Date and time are
/// joined by a space, the way .NET joins them for every region's own date-and-time formats.
///
/// Some calendars cover only a stretch of years, the Umm al-Qura one Saudi Arabia uses among them,
/// and throw for a day outside it. That day is written the invariant way instead, since a date
/// that can't be shown mustn't stop the window showing it from opening.
/// </remarks>
public static class DisplayDates
{
    /// <summary>The format specifiers a date pattern can hold; any other character is copied as it is.</summary>
    private const string Specifiers = "dfFghHKmMstyz";

    /// <summary>A day and month, such as "5 Oct".</summary>
    /// <param name="day">The day</param>
    /// <param name="culture">The culture to write it in, normally the machine's</param>
    /// <returns>The day and month, briefly</returns>
    public static string DayMonth(DateOnly day, CultureInfo culture)
        => Write(culture, f => Brief(f.MonthDayPattern), (pattern, c) => day.ToString(pattern, c));

    /// <summary>A date and time, such as "5 Oct 2026 09:30".</summary>
    /// <param name="moment">The moment, already in the timezone it's to be read in</param>
    /// <param name="culture">The culture to write it in, normally the machine's</param>
    /// <returns>The date, briefly, then the time</returns>
    public static string DateAndTime(DateTime moment, CultureInfo culture)
        => Write(culture, f => Brief(WithoutWeekday(f.LongDatePattern)) + " " + f.ShortTimePattern, (pattern, c) => moment.ToString(pattern, c));

    /// <summary>A moment in the coming days, such as "Mon, 5 Oct 09:30".</summary>
    /// <param name="moment">The moment, already in the timezone it's to be read in</param>
    /// <param name="culture">The culture to write it in, normally the machine's</param>
    /// <returns>The weekday where the region writes one, the day and month, then the time</returns>
    public static string WeekdayAndTime(DateTime moment, CultureInfo culture)
        => Write(culture, f => WithWeekday(f.LongDatePattern, Brief(f.MonthDayPattern)) + " " + f.ShortTimePattern, (pattern, c) => moment.ToString(pattern, c));

    /// <summary>A day in full, as the region writes its long date.</summary>
    /// <param name="day">The day</param>
    /// <param name="culture">The culture to write it in, normally the machine's</param>
    /// <returns>The long date, such as "Monday, 5 October 2026"</returns>
    public static string LongDay(DateOnly day, CultureInfo culture)
        => Write(culture, f => f.LongDatePattern, (pattern, c) => day.ToString(pattern, c));

    /// <summary>
    /// Formats with a culture's pattern, or the invariant one's for a day its calendar can't hold.
    /// </summary>
    /// <param name="culture">The culture to write in</param>
    /// <param name="pattern">The pattern, from a culture's date formats</param>
    /// <param name="format">Writes the date with a pattern and a culture</param>
    /// <returns>The date as written</returns>
    private static string Write(CultureInfo culture, Func<DateTimeFormatInfo, string> pattern, Func<string, CultureInfo, string> format)
    {
        try
        {
            return format(pattern(culture.DateTimeFormat), culture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return format(pattern(CultureInfo.InvariantCulture.DateTimeFormat), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A pattern with its month names abbreviated and its day unpadded.</summary>
    /// <param name="pattern">A date pattern</param>
    /// <returns>The pattern, briefly</returns>
    private static string Brief(string pattern)
        => string.Concat(Pieces(pattern).Select(p => p switch
        {
            { Specifier: 'M', Text.Length: >= 4 } => "MMM",
            { Specifier: 'd', Text.Length: 2 } => "d",
            _ => p.Text,
        }));

    /// <summary>A pattern with its weekday taken out, along with the punctuation that set it off.</summary>
    /// <param name="pattern">A date pattern</param>
    /// <returns>The pattern, without a weekday</returns>
    private static string WithoutWeekday(string pattern)
    {
        var pieces = Pieces(pattern);
        if (Weekday(pieces) is not { } weekday)
            return pattern;

        pieces.RemoveRange(weekday.From, weekday.Count);
        return string.Concat(pieces.Select(p => p.Text));
    }

    /// <summary>A day and month with an abbreviated weekday, placed where a long date places its own.</summary>
    /// <param name="longDate">The region's long date pattern</param>
    /// <param name="dayMonth">The day and month pattern to put the weekday beside</param>
    /// <returns>The day and month, with a weekday when the long date has one</returns>
    private static string WithWeekday(string longDate, string dayMonth)
        => Weekday(Pieces(longDate)) switch
        {
            null => dayMonth,
            { Leads: true } weekday => "ddd" + weekday.Gap + dayMonth,
            { } weekday => dayMonth + weekday.Gap + "ddd",
        };

    /// <summary>
    /// Where a pattern's weekday is, with the spaces and commas between it and the rest.
    /// </summary>
    /// <remarks>
    /// Only spaces and commas count as the gap. A full stop can be part of the date beside it, as in
    /// Hungarian's "d., dddd", and a letter run on from it, as in Thai's "ddddที่".
    /// </remarks>
    /// <param name="pieces">A pattern's pieces</param>
    /// <returns>The weekday and its gap, or null when the pattern has no weekday</returns>
    private static (int From, int Count, string Gap, bool Leads)? Weekday(List<Piece> pieces)
    {
        var at = pieces.FindIndex(p => p is { Specifier: 'd', Text.Length: >= 3 });
        if (at < 0)
            return null;

        var before = at;
        while (before > 0 && IsGap(pieces[before - 1]))
            before--;

        if (before == 0)
        {
            var after = at + 1;
            while (after < pieces.Count && IsGap(pieces[after]))
                after++;

            return (before, after - before, Text(pieces, at + 1, after), Leads: true);
        }

        return (before, at + 1 - before, Text(pieces, before, at), Leads: false);
    }

    private static bool IsGap(Piece piece)
        => piece is { Specifier: '\0', Text: [var c] } && (char.IsWhiteSpace(c) || c is ',' or '،' or '、' or '，');

    private static string Text(List<Piece> pieces, int from, int to)
        => string.Concat(pieces.Skip(from).Take(to - from).Select(p => p.Text));

    /// <summary>
    /// A pattern split into its specifiers and the text between them, so one can be changed without
    /// touching a letter that's only quoted.
    /// </summary>
    /// <param name="pattern">A date pattern</param>
    /// <returns>Each run of one specifier, and each quoted, escaped or plain character between</returns>
    private static List<Piece> Pieces(string pattern)
    {
        var pieces = new List<Piece>();
        for (var i = 0; i < pattern.Length;)
        {
            var c = pattern[i];
            var end = i + 1;

            if (c is '\'' or '"')
            {
                while (end < pattern.Length && pattern[end] != c)
                    end += pattern[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, pattern.Length);
            }
            else if (c == '\\')
            {
                end = Math.Min(i + 2, pattern.Length);
            }
            else if (Specifiers.Contains(c))
            {
                while (end < pattern.Length && pattern[end] == c)
                    end++;
            }

            pieces.Add(new Piece(pattern[i..end], Specifiers.Contains(c) ? c : '\0'));
            i = end;
        }

        return pieces;
    }

    /// <summary>A piece of a date pattern.</summary>
    /// <param name="Text">The piece as written in the pattern</param>
    /// <param name="Specifier">The specifier it repeats, or a null character for text that's copied as it is</param>
    private readonly record struct Piece(string Text, char Specifier);
}
