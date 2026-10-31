using System.Globalization;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// That a date Termyn writes for somebody comes out the way their region arranges one.
/// </summary>
/// <remarks>
/// Every culture here comes from <see cref="CultureInfo.GetCultureInfo(string)"/>, which leaves out
/// whatever the machine running the tests has changed in its region settings. The English ones are
/// checked word for word. The others are checked against the pattern they should come out in,
/// since what's in question is the order, and a region's abbreviations are its own data — German's
/// "Okt." and "Mo" — which can change from one version of ICU to the next.
///
/// English has changed between versions too, and Windows, macOS and Linux each carry their own ICU.
/// Newer ones put a narrow no-break space before AM, which is taken as a space here, and drop the
/// comma after British English's weekday, which can't be: it's punctuation Termyn copies from the
/// long date. So British English is checked against both long dates written out, and against its
/// own for following whichever of them the machine has.
/// </remarks>
public class DisplayDatesTests
{
    private static readonly DateTime Moment = new(2026, 10, 5, 9, 30, 0);

    private static readonly DateOnly Day = DateOnly.FromDateTime(Moment);

    [Theory]
    [InlineData("en-GB", "5 Oct")]
    [InlineData("en-US", "Oct 5")]
    [InlineData("ja-JP", "10月5日")]
    public void A_day_and_month_follow_the_regions_order(string culture, string expected)
        => Assert.Equal(expected, DisplayDates.DayMonth(Day, CultureInfo.GetCultureInfo(culture)));

    [Theory]
    [InlineData("en-GB", "5 Oct 2026 09:30")]
    [InlineData("en-US", "Oct 5, 2026 9:30 AM")]
    [InlineData("ja-JP", "2026年10月5日 9:30")]
    public void A_date_and_time_follow_the_regions_order(string culture, string expected)
        => Assert.Equal(expected, Regions.Spaced(DisplayDates.DateAndTime(Moment, CultureInfo.GetCultureInfo(culture))));

    [Theory]
    [InlineData("en-GB", "dddd, d MMMM yyyy", "Mon, 5 Oct 09:30")]
    [InlineData("en-GB", "dddd d MMMM yyyy", "Mon 5 Oct 09:30")]
    [InlineData("en-US", "dddd, MMMM d, yyyy", "Mon, Oct 5 9:30 AM")]
    public void A_weekday_goes_where_the_regions_long_date_puts_it(string culture, string longDate, string expected)
    {
        // Only the long date is written out: the month and day, the time and the names are the
        // culture's own.
        var region = (CultureInfo)CultureInfo.GetCultureInfo(culture).Clone();
        region.DateTimeFormat.LongDatePattern = longDate;

        Assert.Equal(expected, Regions.Spaced(DisplayDates.WeekdayAndTime(Moment, region)));
    }

    [Fact]
    public void A_weekday_is_punctuated_the_way_the_regions_own_long_date_is()
    {
        // Windows' ICU has "dddd, d MMMM yyyy" for British English, and newer ones, such as the
        // ICU 74 Ubuntu 24.04 ships, have "dddd d MMMM yyyy". Whichever this machine has, the
        // weekday follows it.
        var british = CultureInfo.GetCultureInfo("en-GB");
        var comma = british.DateTimeFormat.LongDatePattern.StartsWith("dddd,", StringComparison.Ordinal);

        Assert.Equal(comma ? "Mon, 5 Oct 09:30" : "Mon 5 Oct 09:30", DisplayDates.WeekdayAndTime(Moment, british));
        Assert.Equal(comma ? "Mon, 5 Oct" : "Mon 5 Oct", DisplayDates.WeekdayDayMonth(Day, british));
    }

    [Fact]
    public void German_puts_its_full_stop_after_the_day()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");

        Assert.Equal(Day.ToString("d. MMM", german), DisplayDates.DayMonth(Day, german));
        Assert.Equal(Moment.ToString("d. MMM yyyy HH:mm", german), DisplayDates.DateAndTime(Moment, german));
        Assert.Equal(Moment.ToString("ddd, d. MMM HH:mm", german), DisplayDates.WeekdayAndTime(Moment, german));
    }

    [Fact]
    public void A_weekday_the_long_date_ends_with_goes_after()
    {
        // Hungarian ends its long date with the weekday, after a comma, and the full stop before
        // the comma belongs to the day.
        var hungarian = CultureInfo.GetCultureInfo("hu-HU");

        Assert.Equal(Moment.ToString("MMM d., ddd H:mm", hungarian), DisplayDates.WeekdayAndTime(Moment, hungarian));
        Assert.Equal(Moment.ToString("yyyy. MMM d. H:mm", hungarian), DisplayDates.DateAndTime(Moment, hungarian));
    }

    [Fact]
    public void Thai_keeps_the_word_that_runs_on_from_its_weekday_with_the_weekday()
    {
        // "ddddที่ d MMMM" is "Monday the 5th October": "ที่" belongs with the weekday, so it goes
        // when the weekday does, and a weekday put back is kept apart from the day by a space.
        var thai = CultureInfo.GetCultureInfo("th-TH");

        Assert.Equal(Moment.ToString("d MMM g yyyy HH:mm", thai), DisplayDates.DateAndTime(Moment, thai));
        Assert.Equal(Moment.ToString("ddd d MMM HH:mm", thai), DisplayDates.WeekdayAndTime(Moment, thai));
    }

    [Fact]
    public void Mongolian_takes_out_the_word_after_its_weekday_with_the_weekday()
    {
        // "Даваа гараг" is Monday, the second word saying it's a day of the week. Left behind
        // without the weekday, it says nothing. The long date is Mongolian's, written out here
        // because ICU's own puts a narrow no-break space after the year, which isn't what's in
        // question.
        var mongolian = (CultureInfo)CultureInfo.GetCultureInfo("mn-MN").Clone();
        mongolian.DateTimeFormat.LongDatePattern = "yyyy 'оны' MMMM'ын' d, dddd 'гараг'";

        Assert.Equal(Moment.ToString("yyyy 'оны' MMM'ын' d HH:mm", mongolian), DisplayDates.DateAndTime(Moment, mongolian));
        Assert.Equal(Moment.ToString("MMM'ын' d, ddd HH:mm", mongolian), DisplayDates.WeekdayAndTime(Moment, mongolian));
    }

    [Fact]
    public void A_weekday_put_back_against_the_day_is_kept_apart_from_it()
    {
        // "ddd" written straight against "d" is one run, which is the full weekday and no day.
        var british = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        british.DateTimeFormat.LongDatePattern = "dddd/d MMMM yyyy";

        Assert.Equal("Mon 5 Oct 09:30", DisplayDates.WeekdayAndTime(Moment, british));
    }

    [Fact]
    public void Text_quoted_in_a_pattern_is_kept_as_written()
    {
        // A letter inside quotes is text, so the "dd" in a quoted word isn't a day for the brief
        // forms to unpad. Windows' region settings take a long date like this from anybody.
        var british = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        british.DateTimeFormat.LongDatePattern = "'Added' dd MMMM yyyy";

        Assert.Equal("Added 5 Oct 2026 09:30", DisplayDates.DateAndTime(Moment, british));
    }

    [Fact]
    public void Spanish_keeps_the_words_its_date_is_written_with()
    {
        var spanish = CultureInfo.GetCultureInfo("es-ES");

        Assert.Equal(Moment.ToString("d 'de' MMM 'de' yyyy H:mm", spanish), DisplayDates.DateAndTime(Moment, spanish));
    }

    [Fact]
    public void A_long_date_set_without_a_weekday_in_the_region_settings_gives_none()
    {
        // Windows' own en-GB long date, which a machine set to it hands over in place of the
        // culture's: no weekday, and the day padded. The brief forms don't pad it.
        var british = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
        british.DateTimeFormat.LongDatePattern = "dd MMMM yyyy";

        Assert.Equal("5 Oct 09:30", DisplayDates.WeekdayAndTime(Moment, british));
        Assert.Equal("5 Oct 2026 09:30", DisplayDates.DateAndTime(Moment, british));
        Assert.Equal("05 October 2026", DisplayDates.LongDay(Day, british));
    }

    [Theory]
    [InlineData("en-GB", "^Monday,? 5 October 2026$")]
    [InlineData("en-US", "^Monday, October 5, 2026$")]
    public void A_day_in_full_is_the_regions_long_date(string culture, string looksLike)
    {
        // The long date as this machine's ICU has it, whose British comma comes and goes with the
        // version, and which still has to read as a long date in the region's order.
        var region = CultureInfo.GetCultureInfo(culture);
        var written = DisplayDates.LongDay(Day, region);

        Assert.Equal(Day.ToString(region.DateTimeFormat.LongDatePattern, region), written);
        Assert.Matches(looksLike, written);
    }

    [Fact]
    public void A_day_the_regions_calendar_cant_hold_is_written_the_invariant_way()
    {
        // Saudi Arabia's Umm al-Qura calendar runs from 1900 to 2077, and formatting outside it throws.
        var saudi = CultureInfo.GetCultureInfo("ar-SA");
        var old = new DateTime(1600, 5, 1, 9, 30, 0);

        Assert.Equal("May 1", DisplayDates.DayMonth(DateOnly.FromDateTime(old), saudi));
        Assert.Equal("1 May 1600 09:30", DisplayDates.DateAndTime(old, saudi));
        Assert.Equal("Mon, May 1 09:30", DisplayDates.WeekdayAndTime(old, saudi));
        Assert.Equal("Monday, 01 May 1600", DisplayDates.LongDay(DateOnly.FromDateTime(old), saudi));
    }
}
