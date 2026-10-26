using System.Globalization;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// That the presenter's own text comes out of the catalogue, and survives a machine that isn't set
/// to English.
/// </summary>
/// <remarks>
/// Most of the wording is asserted where the code that says it is tested. What's here is the
/// catalogue as a whole: that every entry is read and explained, and that nothing goes round it.
/// </remarks>
public class TextCatalogueTests
{
    [Fact]
    public void The_status_line_reads_what_the_catalogue_says()
        => Assert.Equal("Syncing… · 3 pending", new SyncStatus(SyncState.Syncing, Pending: 3).Describe());

    [Fact]
    public void A_counted_phrase_keeps_its_number_where_the_catalogue_puts_it()
    {
        // The placeholder is the half of a moved string that can go wrong quietly: a value whose
        // {0} was lost still compiles, still resolves, and reads "pending" with nothing in front.
        Assert.Contains("1", new SyncStatus(SyncState.Syncing, Pending: 1).Describe());
    }

    [Fact]
    public void It_still_says_something_on_a_machine_set_to_a_language_nobody_has_translated_to()
    {
        // Not what it says, only that it says it — so a translation landing later doesn't send
        // somebody looking for why this went red.
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

            var line = new SyncStatus(SyncState.Syncing, Pending: 3).Describe();

            Assert.NotEmpty(line);
            Assert.Contains("3", line);
        }
        finally
        {
            CultureInfo.CurrentUICulture = was;
        }
    }

    [Fact]
    public void Nothing_in_the_catalogue_is_protocol()
        => Assert.Empty(ProtocolText.InCataloguesOf(typeof(SyncStatus).Assembly));

    [Fact]
    public void Each_sync_state_reads_what_the_catalogue_says()
    {
        Assert.Equal("Offline (showing cached)", new SyncStatus(SyncState.Offline).Describe());
        Assert.Equal("Paused", new SyncStatus(SyncState.Paused).Describe());
        Assert.Equal("Paused (retry in 5s)", new SyncStatus(SyncState.Paused, RetryIn: TimeSpan.FromSeconds(5)).Describe());
        Assert.Equal("Reconnect needed", new SyncStatus(SyncState.ReconnectNeeded).Describe());
        Assert.Equal("Synced", new SyncStatus(SyncState.Synced).Describe());
        Assert.Equal("Synced 3m ago", new SyncStatus(SyncState.Synced, Since: TimeSpan.FromMinutes(3)).Describe());
        Assert.Equal("Not synced yet", new SyncStatus(SyncState.Never).Describe());
    }

    [Fact]
    public void How_long_ago_reads_what_the_catalogue_says()
    {
        Assert.Equal("just now", SyncStatus.Ago(TimeSpan.FromSeconds(2)));
        Assert.Equal("30s ago", SyncStatus.Ago(TimeSpan.FromSeconds(30)));
        Assert.Equal("3m ago", SyncStatus.Ago(TimeSpan.FromMinutes(3)));
        Assert.Equal("5h ago", SyncStatus.Ago(TimeSpan.FromHours(5)));
        Assert.Equal("2d ago", SyncStatus.Ago(TimeSpan.FromDays(2)));
    }

    // ---- The catalogue as it's written -------------------------------------------------------------

    private static readonly CatalogueSource Source = new("Termyn.Presentation");

    [Fact]
    public void Every_entry_tells_a_translator_where_it_appears()
        => Assert.Empty(Source.Bare());

    [Fact]
    public void Every_placeholder_is_explained()
        => Assert.Empty(Source.Unexplained());

    [Fact]
    public void Every_entry_is_used()
        => Assert.Empty(Source.Unused(Plurals.LanguageEntry));

    [Fact]
    public void Nothing_said_to_a_person_is_written_into_the_code()
    {
        // The sidebar's headings are named in the code for settings to remember a folded one by,
        // and that name has to stay put whatever language the heading's shown in: the heading's
        // own words come from the catalogue beside it.
        var found = Source.InlineProse("Favourites", "Projects", "Labels", "Filters");

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("""var s = "Not sent yet";""")]
    [InlineData("""var s = "Today";""")]
    [InlineData("""var s = $"{name} (still processing)";""")]
    [InlineData("""var s = $"{m}m before";""")]
    [InlineData("""var s = $"{bytes} KB";""")]
    [InlineData("""var s = $"{day} at {time}";""")]
    [InlineData("""var s = $"Task: {row.Content}";""")]
    [InlineData("""var s = $"{(done ? "Ticked off" : "")}";""")]
    [InlineData("""throw new CaptureRefusedException("That can't be added");""")]
    public void Text_for_a_person_written_into_the_code_is_seen(string code)
    {
        // The check above expects nothing, so one that read nothing would pass it too.
        Assert.Single(CatalogueSource.InlineProseIn(code));
    }

    [Theory]
    [InlineData("""_log?.Warn("Todoist couldn't be reached.");""")]
    [InlineData("""_log.Info($"Synced {count} items");""")]
    [InlineData("""throw new ArgumentException("isn't the _Other form of a counted phrase", nameof(name));""")]
    [InlineData("""[assembly: InternalsVisibleTo("Termyn.Presentation.Tests")]""")]
    [InlineData("""var s = "item_add";""")]
    [InlineData("""const string OtherSuffix = "_Other";""")]
    [InlineData("""var s = "content";""")]
    [InlineData("""var s = "d MMM yyyy";""")]
    [InlineData("""var s = $"{when} {file}";""")]
    [InlineData("""// var s = "Not sent yet";""")]
    public void Text_that_isnt_for_a_person_is_left_alone(string code)
        => Assert.Empty(CatalogueSource.InlineProseIn(code));
}
