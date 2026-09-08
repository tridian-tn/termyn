using System.Globalization;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// That the presenter's own text comes out of the catalogue, and survives a machine that isn't set
/// to English.
/// </summary>
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
}
