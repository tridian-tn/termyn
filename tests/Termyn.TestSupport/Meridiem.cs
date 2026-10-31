using System.Globalization;

namespace Termyn.TestSupport;

/// <summary>
/// Writes an expected 12-hour time with the space the machine's own region data puts before AM or
/// PM.
/// </summary>
/// <remarks>
/// That space is region data like any other, and it isn't the same everywhere: Windows writes an
/// ordinary space, and the ICU that macOS and Linux bring writes a narrow no-break space (U+202F),
/// as CLDR has since version 42. The two look identical in a failure message. A test that writes
/// "9:30 AM" out in full is checking the order of a date, not which of those it got, so it says
/// what it expects with an ordinary space and passes it through here.
/// </remarks>
public static class Meridiem
{
    /// <summary>Puts the region's space before every AM and PM in an expected string.</summary>
    /// <param name="expected">The expected text, with an ordinary space before AM or PM</param>
    /// <param name="culture">The culture the text was written in</param>
    /// <returns>The text as that culture writes it on this machine</returns>
    public static string AsWritten(string expected, CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortTimePattern;
        var at = pattern.IndexOf('t');
        var space = at > 0 && char.IsWhiteSpace(pattern[at - 1]) ? pattern[at - 1] : ' ';

        return expected.Replace(" AM", space + "AM").Replace(" PM", space + "PM");
    }
}
