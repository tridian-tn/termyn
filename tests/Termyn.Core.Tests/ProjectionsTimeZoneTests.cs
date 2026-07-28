using Termyn.Core.Model;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

public class ProjectionsTimeZoneTests
{
    [Theory]
    [InlineData("Pacific/Auckland")]
    [InlineData("Europe/London")]
    [InlineData("America/New_York")]
    public void The_accounts_iana_timezone_resolves(string timezone)
    {
        // Todoist reports IANA names, which need ICU — with invariant globalization these all fall
        // back to the machine's zone and the account's date is silently ignored.
        var json = $$$"""{"id":"user","tz_info":{"timezone":"{{{timezone}}}"}}""";
        var zone = Projections.ToTimeZone(Json.Object(json));

        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById(timezone).BaseUtcOffset, zone.BaseUtcOffset);
    }

    [Fact]
    public void An_unknown_or_absent_timezone_falls_back_to_the_machines()
    {
        Assert.Equal(TimeZoneInfo.Local.Id, Projections.ToTimeZone(null).Id);
        Assert.Equal(TimeZoneInfo.Local.Id, Projections.ToTimeZone(Json.Object("""{"id":"user"}""")).Id);
        Assert.Equal(TimeZoneInfo.Local.Id, Projections.ToTimeZone(Json.Object("""{"id":"user","tz_info":{"timezone":"Mars/Olympus"}}""")).Id);
    }
}
