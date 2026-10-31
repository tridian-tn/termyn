using System.Globalization;

namespace Termyn.TestSupport;

/// <summary>
/// Regions with what ICU varies pinned down: English ones with the date and time patterns written
/// out, so a row checked word for word reads the same whichever version of ICU the machine has, and
/// a Saudi one with its calendar set.
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
    /// Saudi Arabian Arabic on the Umm al-Qura calendar, which runs only from 1900 to 2077, or null
    /// where this machine's ICU doesn't offer that calendar for the region.
    /// </summary>
    /// <remarks>
    /// Set rather than taken as the region's default, which isn't the same everywhere: Windows and
    /// macOS give ar-SA Umm al-Qura, and Linux's ICU gives it the Gregorian calendar, which holds
    /// any year. A test of a day the region's calendar can't hold needs one that can't.
    /// </remarks>
    public static CultureInfo? UmmAlQura { get; } = OnUmmAlQura();

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

    private static CultureInfo? OnUmmAlQura()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone();
        if (culture.OptionalCalendars.OfType<UmAlQuraCalendar>().FirstOrDefault() is not { } calendar)
            return null;

        culture.DateTimeFormat.Calendar = calendar;
        return CultureInfo.ReadOnly(culture);
    }

    private static CultureInfo Pinned(string name, string longDate, string monthDay, string shortTime)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(name).Clone();
        culture.DateTimeFormat.LongDatePattern = longDate;
        culture.DateTimeFormat.MonthDayPattern = monthDay;
        culture.DateTimeFormat.ShortTimePattern = shortTime;
        return CultureInfo.ReadOnly(culture);
    }
}
