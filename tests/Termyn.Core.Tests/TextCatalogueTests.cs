using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using System.Text.Json.Serialization;
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
            async () => await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));

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
                async () => await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));

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
    public void Code_that_speaks_protocol_never_reads_a_catalogue(Type type)
    {
        // What a keyword moved into a catalogue looks like from here, however it's worded — the
        // parser comparing what it reads against a lookup that a translation will change.
        Assert.Empty(ProtocolText.CatalogueReadsIn(type));
    }

    /// <summary>Every type in the settings code, the colours Todoist names included.</summary>
    public static TheoryData<Type> SettingsCode =>
    [
        .. typeof(SettingsStore).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(SettingsStore).Namespace && !t.IsNested && !t.IsEnum),
    ];

    [Theory]
    [MemberData(nameof(SettingsCode))]
    public void The_settings_code_never_reads_a_catalogue(Type type)
    {
        // It reads and writes the settings file's keys by name, "view" and "theme" among them, and
        // those are words a catalogue can hold. The whole namespace rather than a list, so a
        // migration or a helper added beside the store is held to it without anyone remembering.
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

    /// <summary>
    /// Words of the grammar, and every settings key that's one lower-case word, taken from the
    /// settings so a key added later is covered too.
    /// </summary>
    public static TheoryData<string> PlainWords =>
    [
        "day", "today", "every", "no date",
        .. ProtocolText.SettingsKeys(typeof(AppSettings), typeof(ViewState)).Where(key => key.All(char.IsAsciiLetterLower)),
    ];

    [Theory]
    [MemberData(nameof(PlainWords))]
    public void A_plain_word_is_left_to_the_check_on_the_code(string word)
    {
        // The reminder list already says "day" to people, and the palette calls a built-in view a
        // "view". Flagged here, a catalogue couldn't take them — and the parser or the settings
        // store reading one is caught above anyway.
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
        // Worked out from the others and left out of the file. A type of its own, since each such
        // property in the app's settings is either one word or caught by the camelCase pattern, so
        // dropping the rule would pass unseen there.
        Assert.Equal(
            ["sync-mode", "theme", "windowX"],
            ProtocolText.SettingsKeys(typeof(SampleSettings)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_a_key_that_is_one_lower_case_word_is_left_off_the_list()
    {
        // "sync-mode" has no capital either, but it's neither snake_case nor camelCase, so nothing
        // else would catch it.
        Assert.Equal(
            ["sync-mode", "windowX"],
            ProtocolText.SettingsKeysOnSight(typeof(SampleSettings)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_settings_key_the_camelCase_pattern_misses_is_still_protocol()
    {
        // It ends in a lone capital, so only being on the list catches it.
        Assert.True(ProtocolText.IsProtocol("windowX"));
    }

    [Fact]
    public void A_catalogue_that_is_not_there_is_not_read_as_an_empty_one()
    {
        // Or a catalogue renamed out from under the check would leave it passing on nothing.
        Assert.Throws<MissingManifestResourceException>(() => ProtocolText.InCataloguesOf(typeof(ProtocolText).Assembly));
    }

    [Fact]
    public void The_messages_the_app_shows_read_what_the_catalogue_says()
    {
        // Verbatim, so a reworded or mistyped entry fails here rather than in the dialog a user is
        // the first to read it in. Only the unreachable-host message above is proved by driving the
        // path; the rest are many separate HTTP conditions, and their wording is what's at risk.
        Assert.Equal("Could not reach Todoist.", Strings.CouldNotReachTodoist);
        Assert.Equal("Todoist rejected the API token.", Strings.TokenRejected);
        Assert.Equal("The Todoist request timed out.", Strings.RequestTimedOut);
        Assert.Equal("Todoist returned an unreadable answer to the token check.", Strings.UnreadableTokenCheck);
        Assert.Equal("Todoist returned an unexpected sync response.", Strings.UnexpectedSyncResponse);
        Assert.Equal("Todoist returned an unreadable sync response.", Strings.UnreadableSyncResponse);
        Assert.Equal("Todoist returned an unexpected completed-tasks response.", Strings.UnexpectedCompletedResponse);
        Assert.Equal("Todoist returned an unreadable completed-tasks response.", Strings.UnreadableCompletedResponse);
        Assert.Equal("The download from Todoist failed part-way.", Strings.DownloadFailedPartWay);
        Assert.Equal("The file's address isn't a secure web address, so Termyn won't download from it.", Strings.UnsafeFileAddress);
        Assert.Equal("Todoist returned an unexpected upload response.", Strings.UnexpectedUploadResponse);
        Assert.Equal("Todoist returned an unreadable upload response.", Strings.UnreadableUploadResponse);
        Assert.Equal("Todoist returned HTTP {0}.", Strings.UnexpectedHttpStatus);
        Assert.Equal("Todoist turned the token away. Reconnect to make changes.", Strings.TokenTurnedAway);
        Assert.Equal("Todoist did not report a result for this change.", Strings.NoResultForChange);
        Assert.Equal("Termyn isn't signed in to Todoist.", Strings.NotSignedIn);
    }

    /// <summary>
    /// Settings with one of each kind of key: a single word, camelCase with a lone capital, a name
    /// of its own with no capital, and a property the file never holds.
    /// </summary>
    private sealed class SampleSettings
    {
        public string Theme { get; init; } = "";

        public int WindowX { get; init; }

        [JsonPropertyName("sync-mode")]
        public int SyncMode { get; init; }

        [JsonIgnore]
        public int ClampedX => WindowX;
    }

    /// <summary>A host that can't be got to, which is what a dead link looks like from here.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no route", null, HttpStatusCode.ServiceUnavailable);
    }
}
