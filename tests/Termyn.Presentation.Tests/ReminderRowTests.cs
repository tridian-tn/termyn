using System.Globalization;
using Termyn.Core.Model;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// A task's reminders as the reminder dialog lists them, and which of them it offers to remove.
/// </summary>
public class ReminderRowTests
{
    private static ReminderRow Row(ReminderKind kind, int minutes = 0, string? due = null, string? place = null, TimeZoneInfo? zone = null)
        => new(new Reminder { Id = "r1", ItemId = "i1", Kind = kind, MinuteOffset = minutes, DueDate = due, LocationName = place }, zone ?? TimeZoneInfo.Utc);

    [Fact]
    public void One_at_the_due_time_says_so_rather_than_nought_minutes_before()
        => Assert.Equal("When it's due", Row(ReminderKind.Relative).ToString());

    [Theory]
    [InlineData(1, "1 minute before it's due")]
    [InlineData(30, "30 minutes before it's due")]
    [InlineData(60, "1 hour before it's due")]
    [InlineData(120, "2 hours before it's due")]
    [InlineData(1440, "1 day before it's due")]
    [InlineData(2880, "2 days before it's due")]
    public void An_offset_is_said_in_the_largest_unit_it_divides_into(int minutes, string expected)
        => Assert.Equal(expected, Row(ReminderKind.Relative, minutes).ToString());

    [Theory]
    [InlineData(90, "90 minutes before it's due")]
    [InlineData(1500, "1500 minutes before it's due")]
    public void An_odd_offset_set_elsewhere_still_reads_properly(int minutes, string expected)
    {
        // The web app sets whatever it likes, and an hour and a half isn't a whole number of hours.
        Assert.Equal(expected, Row(ReminderKind.Relative, minutes).ToString());
    }

    [Fact]
    public void An_absolute_one_reads_as_the_moment_rather_than_the_timestamp()
    {
        // Formatted in the machine's own culture, so this sets one rather than depending on the
        // machine it runs on.
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = Regions.British;

            Assert.Equal("At Fri, 14 Aug 09:30", Row(ReminderKind.Absolute, due: "2026-08-14T09:30:00").ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void One_set_in_a_fixed_timezone_is_shown_in_the_accounts()
    {
        // Todoist sends it as a UTC instant. 13:00 UTC is 09:00 in New York in October, whatever
        // timezone the machine showing it is in.
        using var british = new CultureScope(Regions.British);
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.Equal("At Sat, 24 Oct 09:00", Row(ReminderKind.Absolute, due: "2026-10-24T13:00:00Z", zone: newYork).ToString());
    }

    [Fact]
    public void An_absolute_one_whose_time_cant_be_read_is_shown_as_the_server_wrote_it()
    {
        Assert.Equal("At sometime", Row(ReminderKind.Absolute, due: "sometime").ToString());
        Assert.Equal("At a set time", Row(ReminderKind.Absolute).ToString());
    }

    [Fact]
    public void A_repeating_one_reads_as_its_schedule_and_when_it_next_fires()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = Regions.British;
            var row = new ReminderRow(new Reminder
            {
                Id = "r1", ItemId = "i1", Kind = ReminderKind.Absolute, DueDate = "2026-10-24T09:00:00", Schedule = "every sat 9am",
            }, TimeZoneInfo.Utc);

            // Not "At Sat, 24 Oct", which would say it fires once.
            Assert.Equal("Every sat 9am (next Sat, 24 Oct 09:00)", row.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void A_repeating_one_the_server_hasnt_answered_for_reads_as_its_schedule_alone()
    {
        var row = new ReminderRow(new Reminder { Id = "r1", ItemId = "i1", Kind = ReminderKind.Absolute, Schedule = "every sat 9am" }, TimeZoneInfo.Utc);

        Assert.Equal("Every sat 9am", row.ToString());
    }

    [Fact]
    public void A_location_one_names_the_place_and_where_it_was_set()
    {
        Assert.Equal("At Home (set in Todoist)", Row(ReminderKind.Location, place: "Home").ToString());
        Assert.Equal("At a place (set in Todoist)", Row(ReminderKind.Location).ToString());
    }

    [Fact]
    public void A_kind_Termyn_doesnt_know_is_still_listed()
        => Assert.Equal("A reminder set in Todoist", Row(ReminderKind.Unknown).ToString());

    [Theory]
    [InlineData(ReminderKind.Relative, true)]
    [InlineData(ReminderKind.Absolute, true)]
    [InlineData(ReminderKind.Location, false)]
    [InlineData(ReminderKind.Unknown, false)]
    public void Only_a_kind_Termyn_could_put_back_is_offered_for_removal(ReminderKind kind, bool removable)
    {
        // Removing one it can't author is a one-way door.
        Assert.Equal(removable, Row(kind).CanRemove);
    }
}
