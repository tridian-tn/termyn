using System.Globalization;
using System.Net;
using System.Reflection;
using System.Resources;
using Termyn.Core.Api;

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

    /// <summary>A host that can't be got to, which is what a dead link looks like from here.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no route", null, HttpStatusCode.ServiceUnavailable);
    }
}
