using System.Net;
using System.Text;
using Termyn.Core.Update;

namespace Termyn.Core.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(" v2.0 ", "2.0")]
    [InlineData("v1.0.0.4", "1.0.0.4")]
    public void Reads_a_release_tag_as_a_version(string tag, string expected)
        => Assert.Equal(Version.Parse(expected), GitHubReleaseCheck.ParseVersion(tag));

    [Theory]
    [InlineData("v1.2.0-beta.1")]   // can't be ordered against the running build
    [InlineData("nightly")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Refuses_a_tag_it_cannot_order(string? tag)
        => Assert.Null(GitHubReleaseCheck.ParseVersion(tag));

    [Fact]
    public void A_newer_release_is_one_that_sorts_above_what_is_running()
    {
        var result = new UpdateResult(new Version(1, 2, 0), "https://example.test/1.2.0");

        Assert.True(result.IsNewerThan(new Version(1, 1, 9)));
        Assert.False(result.IsNewerThan(new Version(1, 2, 0)));
        Assert.False(result.IsNewerThan(new Version(1, 3, 0)));
    }

    [Fact]
    public void Not_knowing_is_never_newer()
        => Assert.False(UpdateResult.Unknown.IsNewerThan(new Version(0, 1)));
}

public class GitHubReleaseCheckTests
{
    [Fact]
    public async Task Reads_the_version_and_the_page_to_send_the_user_to()
    {
        var check = Returning(HttpStatusCode.OK, """
        { "tag_name": "v1.4.0", "html_url": "https://github.com/tridian-tn/termyn/releases/tag/v1.4.0" }
        """);

        var result = await check.LatestAsync();

        Assert.Equal(new Version(1, 4, 0), result.Latest);
        Assert.Equal("https://github.com/tridian-tn/termyn/releases/tag/v1.4.0", result.ReleaseUrl);
    }

    [Fact]
    public async Task Asks_for_the_latest_release_and_names_itself()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, """{"tag_name":"v1.0.0"}"""));
        var check = new GitHubReleaseCheck(new HttpClient(handler));

        await check.LatestAsync();

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal(GitHubReleaseCheck.DefaultEndpoint, handler.LastRequest.RequestUri!.ToString());

        // GitHub refuses anonymous requests without one, and it carries nothing but the product name.
        Assert.Equal("Termyn", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Sends_nothing_about_the_account()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, """{"tag_name":"v1.0.0"}"""));

        await new GitHubReleaseCheck(new HttpClient(handler)).LatestAsync();

        // No body, no authorization, no cookies — a check for a version number is not a report.
        Assert.Null(handler.LastRequest!.Content);
        Assert.Null(handler.LastRequest.Headers.Authorization);
        Assert.Empty(handler.LastRequest.Headers.Where(h => h.Key.Contains("Cookie", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(handler.LastRequest.RequestUri!.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]         // rate-limited, which GitHub answers with 403
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_unhelpful_answer_means_not_knowing_rather_than_failing(HttpStatusCode status)
        => Assert.Equal(UpdateResult.Unknown, await Returning(status, "{}").LatestAsync());

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{}")]                              // no tag at all
    [InlineData("""{"tag_name":"nightly"}""")]      // a tag that can't be ordered
    public async Task An_unreadable_answer_means_not_knowing(string body)
        => Assert.Equal(UpdateResult.Unknown, await Returning(HttpStatusCode.OK, body).LatestAsync());

    [Fact]
    public async Task Being_offline_means_not_knowing()
    {
        var check = new GitHubReleaseCheck(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))));

        Assert.Equal(UpdateResult.Unknown, await check.LatestAsync());
    }

    [Fact]
    public async Task A_timeout_means_not_knowing()
    {
        var check = new GitHubReleaseCheck(new HttpClient(new StubHandler(_ => throw new TaskCanceledException("timeout"))));

        Assert.Equal(UpdateResult.Unknown, await check.LatestAsync());
    }

    [Fact]
    public async Task The_caller_cancelling_is_not_swallowed()
    {
        var check = new GitHubReleaseCheck(new HttpClient(new StubHandler(_ => throw new TaskCanceledException())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.LatestAsync(cts.Token));
    }

    private static GitHubReleaseCheck Returning(HttpStatusCode status, string json)
        => new(new HttpClient(new StubHandler(_ => Resp(status, json))));

    private static HttpResponseMessage Resp(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }
}
