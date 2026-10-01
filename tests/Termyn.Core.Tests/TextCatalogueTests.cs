using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Filters;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// That the text Core says to a person comes out of the catalogue and not out of the assembly by
/// accident.
/// </summary>
/// <remarks>
/// A string moved to a .resx compiles whether or not the resource is embedded, named right, or
/// findable at run time — the generated accessor is there either way and hands back null when the
/// lookup fails. So the wiring is only proved by asking for the string while the app is running,
/// which is what these do.
/// </remarks>
public class TextCatalogueTests
{
    [Fact]
    public async Task What_it_says_when_Todoist_cannot_be_reached_comes_from_the_catalogue()
    {
        var client = new TodoistApiClient(new HttpClient(new UnreachableHandler()));

        var thrown = await Assert.ThrowsAsync<TodoistNetworkException>(
            async () => await client.SyncAsync("tok", "*", ["items"], []));

        Assert.Equal("Could not reach Todoist.", thrown.Message);
    }

    [Fact]
    public async Task It_still_says_something_on_a_machine_set_to_a_language_nobody_has_translated_to()
    {
        // The failure this rules out is a MissingManifestResourceException, which is what a
        // mis-declared resource gives on the first machine that isn't set to English — and which
        // would surface here as an empty message on an error dialog, or as no dialog at all.
        //
        // What it says isn't asserted, only that it says something: the day a French translation
        // lands this should go on passing rather than needing to be found and changed.
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

            var client = new TodoistApiClient(new HttpClient(new UnreachableHandler()));
            var thrown = await Assert.ThrowsAsync<TodoistNetworkException>(
                async () => await client.SyncAsync("tok", "*", ["items"], []));

            Assert.NotEmpty(thrown.Message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = was;
        }
    }

    [Fact]
    public void The_language_the_strings_are_written_in_is_declared()
    {
        // What lets a machine set to any culture reach these without first going looking for a
        // satellite assembly that was never built. Nothing fails outright without it, which is why
        // it is the sort of setting that gets dropped in a merge and isn't missed.
        var declared = typeof(TodoistApiClient).Assembly
            .GetCustomAttribute<NeutralResourcesLanguageAttribute>();

        Assert.Equal("en-GB", declared?.CultureName);
    }

    [Fact]
    public void Nothing_in_the_catalogue_is_protocol()
        => Assert.Empty(ProtocolText.InCataloguesOf(typeof(TodoistApiClient).Assembly));

    [Theory]
    [InlineData(typeof(FilterParser))]
    [InlineData(typeof(QuickAddParser))]
    [InlineData(typeof(Projections))]
    [InlineData(typeof(ItemFields))]
    [InlineData(typeof(ResourceType))]
    [InlineData(typeof(TodoistPalette))]
    public void Code_that_speaks_protocol_never_reads_a_catalogue(Type type)
    {
        // What a keyword moved into a catalogue looks like from here, however it's worded — the
        // parser comparing what it reads against a lookup that a translation will change.
        Assert.Empty(ProtocolText.CatalogueReadsIn(type));
    }

    [Fact]
    public void A_catalogue_read_is_seen_wherever_the_compiler_put_it()
    {
        // The client's one is inside an async method, so it's compiled into a state machine nested
        // in the class rather than into the class itself. Seeing it is what makes the check above
        // worth anything.
        Assert.NotEmpty(ProtocolText.CatalogueReadsIn(typeof(TodoistApiClient)));
    }

    [Theory]
    [InlineData("assigned to: me")]
    [InlineData(":to_me:")]
    [InlineData("search: {0}")]
    [InlineData("#Work & due before: {0}")]
    [InlineData("p1")]
    [InlineData("database disk image is malformed")]
    [InlineData("item_add")]
    [InlineData("responsible_uid")]
    [InlineData("grape")]
    [InlineData("items")]
    [InlineData("sidebarWidth")]
    [InlineData("yyyy-MM-dd")]
    [InlineData("DELETE FROM outbox WHERE uuid = $uuid")]
    [InlineData("select json from resources where id = $id")]
    [InlineData("select json\nfrom resources")]
    [InlineData(" od \n")]
    public void Protocol_is_told_apart_from_prose(string text)
        => Assert.True(ProtocolText.IsProtocol(text), $"'{text}' should read as protocol");

    [Theory]
    [InlineData("Today")]
    [InlineData("Search…")]
    [InlineData("Could not reach Todoist.")]
    [InlineData("{0} pending")]
    [InlineData("advancing…")]
    [InlineData("Select a project")]
    [InlineData("Select one from the list")]
    [InlineData("Berry red")]
    [InlineData("Due: {0}")]
    [InlineData("Move to: {0}")]
    [InlineData("iCal")]
    [InlineData("macOS")]
    [InlineData("ddd d MMM, HH:mm")]
    public void Prose_is_told_apart_from_protocol(string text)
        => Assert.False(ProtocolText.IsProtocol(text), $"'{text}' should read as prose");

    [Theory]
    [InlineData("day")]
    [InlineData("today")]
    [InlineData("every")]
    [InlineData("no date")]
    public void A_plain_word_of_the_grammar_is_left_to_the_check_on_the_code(string word)
    {
        // The reminder list already says "day" to people. Flagged here, a catalogue couldn't take it
        // when the presentation text moves — and the parser reading one is caught above anyway.
        Assert.False(ProtocolText.IsProtocol(word));
    }

    [Fact]
    public void Protocol_in_a_catalogue_is_reported_and_nothing_else_is()
    {
        // The checks above all expect nothing, so one that read nothing would pass them too. This
        // assembly's own sample has a protocol entry beside prose and a designer's bookkeeping.
        var found = ProtocolText.InCataloguesOf(typeof(TextCatalogueTests).Assembly);

        Assert.Equal(["Termyn.Core.Tests.ProtocolSample.resources: CloseCommand = \"item_close\""], found);
    }

    [Fact]
    public void A_setting_the_file_never_holds_is_not_a_settings_key()
    {
        // Worked out from the others and left out of the file, so "cadence" in a catalogue is just
        // a word.
        Assert.False(ProtocolText.IsProtocol("cadence"));
        Assert.True(ProtocolText.IsProtocol("theme"));
    }

    [Fact]
    public void A_catalogue_that_is_not_there_is_not_read_as_an_empty_one()
    {
        // Or a catalogue renamed out from under the check would leave it passing on nothing.
        Assert.Throws<MissingManifestResourceException>(() => ProtocolText.InCataloguesOf(typeof(ProtocolText).Assembly));
    }

    /// <summary>A host that can't be got to, which is what a dead link looks like from here.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no route", null, HttpStatusCode.ServiceUnavailable);
    }
}
