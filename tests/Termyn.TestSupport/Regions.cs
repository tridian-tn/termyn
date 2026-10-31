using System.Globalization;

namespace Termyn.TestSupport;

/// <summary>
/// English regions with the date and time patterns written out, so a row checked word for word
/// reads the same whichever version of ICU the machine has.
/// </summary>
/// <remarks>
/// A region's patterns are ICU's data, and they move between versions: newer ones drop the comma
/// after British English's weekday ("Monday 5 October 2026") and put a narrow no-break space before
/// American English's AM and PM. Windows carries its own ICU, macOS another and Linux whichever the
/// distribution ships, so the same test sees different data on each. A test of a row or a dialog is
/// about which moment it shows and that it takes the region's arrangement, not about what ICU says
/// that arrangement is this year, so it uses one of these. Whether the arrangement is taken from
/// ICU's own patterns properly, whatever they are, is DisplayDatesTests' business, against the
/// cultures themselves.
///
/// Only the patterns are pinned. Day and month names stay the culture's; the ones these tests use
/// haven't changed.
/// </remarks>
public static class Regions
{
    /// <summary>British English, with a comma after the weekday in its long date, as Windows has it at the time of writing.</summary>
    public static CultureInfo British { get; } = Pinned("en-GB", "dddd, d MMMM yyyy", "d MMMM", "HH:mm");

    /// <summary>American English, with a plain space before AM and PM, as Windows has it at the time of writing.</summary>
    public static CultureInfo American { get; } = Pinned("en-US", "dddd, MMMM d, yyyy", "MMMM d", "h:mm tt");

    /// <summary>
    /// Text with the no-break spaces ICU writes in places as plain ones.
    /// </summary>
    /// <remarks>
    /// For a check against a culture as ICU has it: a narrow no-break space before AM reads the same
    /// as a space, and which one a region uses is its data rather than anything Termyn arranges.
    /// </remarks>
    /// <param name="text">Text written with a culture's patterns</param>
    /// <returns>The text with plain spaces</returns>
    public static string Spaced(string text)
        => text.Replace(' ', ' ').Replace(' ', ' ');

    private static CultureInfo Pinned(string name, string longDate, string monthDay, string shortTime)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(name).Clone();
        culture.DateTimeFormat.LongDatePattern = longDate;
        culture.DateTimeFormat.MonthDayPattern = monthDay;
        culture.DateTimeFormat.ShortTimePattern = shortTime;
        return CultureInfo.ReadOnly(culture);
    }
}
