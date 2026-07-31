using System.Net;
using System.Text;
using Termyn.Core;
using Termyn.Core.Update;

namespace Termyn.Core.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData(" v2.0 ", "2.0.0")]
    [InlineData("v1.0.0.4", "1.0.0")]   // the revision is dropped, so it can be compared and printed
    public void Reads_a_release_tag_as_a_version(string tag, string expected)
        => Assert.Equal(Version.Parse(expected), GitHubReleaseCheck.ParseVersion(tag));

    [Theory]
    [InlineData("v1.2.0-beta.1")]   // can't be ordered against the running build
    [InlineData("nightly")]
    [InlineData("1.2.3+build.7")]   // SemVer build metadata isn't something Version reads
    [InlineData("v99999999999.0.0")]
    [InlineData("v")]
    [InlineData("v1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Refuses_a_tag_it_cannot_order(string? tag)
        => Assert.Null(GitHubReleaseCheck.ParseVersion(tag));

    [Fact]
    public void A_tag_always_comes_back_with_three_numbers()
    {
        // Anything shorter can't be written with ToString(3), and anything longer sorts above the
        // running build on a component nobody meant to compare.
        foreach (var tag in new[] { "v2.0", "v2.0.0", "v2.0.0.0" })
            Assert.Equal(new Version(2, 0, 0), GitHubReleaseCheck.ParseVersion(tag));
    }

    [Fact]
    public void A_four_component_tag_is_not_mistaken_for_an_update()
    {
        // Version treats an absent component as -1, so 1.0.0.0 sorted above the 1.0.0 running and
        // offered an update to the build already installed.
        var result = new UpdateResult(GitHubReleaseCheck.ParseVersion("v1.0.0.0"), null);

        Assert.False(result.IsNewerThan(new Version(1, 0, 0)));
    }

    [Fact]
    public void A_two_component_tag_is_ordered_the_way_a_person_would_read_it()
    {
        var result = new UpdateResult(GitHubReleaseCheck.ParseVersion("v1.2"), null);

        Assert.False(result.IsNewerThan(new Version(1, 2, 0)));
        Assert.True(result.IsNewerThan(new Version(1, 1, 9)));
    }

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

    [Fact]
    public void A_version_is_always_written_with_three_numbers()
    {
        // ToString(3) throws on a version carrying fewer, and versions arrive here from typed tags.
        Assert.Equal("v2.0.0", UpdateResult.Tag(new Version(2, 0)));
        Assert.Equal("v1.2.3", UpdateResult.Tag(new Version(1, 2, 3)));
        Assert.Equal("v1.2.3", UpdateResult.Tag(new Version(1, 2, 3, 4)));
    }

    [Fact]
    public void Not_knowing_says_so_and_offers_nothing()
    {
        var advice = UpdateResult.Unknown.Advise(new Version(1, 0, 0));

        Assert.Contains("Couldn't reach", advice.Message);
        Assert.Contains("v1.0.0", advice.Message);
        Assert.Null(advice.OpenUrl);
    }

    [Fact]
    public void Being_up_to_date_names_the_version_that_is_running()
    {
        // Different numbers on the two sides, because with them equal this can't tell Tag(running)
        // from Tag(latest) — and running ahead of the published release is the normal state on the
        // machine of whoever builds it.
        var advice = new UpdateResult(new Version(1, 0, 0), "https://github.com/x/y").Advise(new Version(1, 1, 0));

        Assert.Equal("Termyn v1.1.0 is the latest.", advice.Message);
        Assert.Null(advice.OpenUrl);
    }

    [Fact]
    public void The_page_we_fall_back_to_is_one_we_would_actually_open()
    {
        // The fallback only runs for a release published without a page of its own, so a bad
        // constant here would dead-end on a dialog whose OK does nothing, and ship unnoticed.
        Assert.Equal(UpdateResult.ReleasesPage, GitHubReleaseCheck.SafeUrl(UpdateResult.ReleasesPage));
        Assert.Equal(UpdateResult.ReleasesPage, Links.Openable(UpdateResult.ReleasesPage));
    }

    [Fact]
    public void A_release_naming_an_empty_page_falls_back_like_one_naming_none()
    {
        var advice = new UpdateResult(new Version(1, 4, 0), "").Advise(new Version(1, 0, 0));

        Assert.Equal(UpdateResult.ReleasesPage, advice.OpenUrl);
    }

    [Fact]
    public void Being_up_to_date_offers_nothing()
    {
        var advice = new UpdateResult(new Version(1, 0, 0), "https://github.com/x/y").Advise(new Version(1, 0, 0));

        Assert.Contains("is the latest", advice.Message);
        Assert.Null(advice.OpenUrl);
    }

    [Fact]
    public void An_update_names_both_versions_and_where_to_read_about_it()
    {
        var advice = new UpdateResult(new Version(1, 4, 0), "https://github.com/tridian-tn/termyn/releases/tag/v1.4.0")
            .Advise(new Version(1, 0, 0));

        Assert.Contains("v1.4.0 is available", advice.Message);
        Assert.Contains("You have v1.0.0", advice.Message);
        Assert.Equal("https://github.com/tridian-tn/termyn/releases/tag/v1.4.0", advice.OpenUrl);
    }

    [Fact]
    public void An_update_with_no_page_of_its_own_still_has_somewhere_to_send_you()
    {
        // Otherwise the dialog offers to open a release page and the OK button does nothing.
        var advice = new UpdateResult(new Version(1, 4, 0), null).Advise(new Version(1, 0, 0));

        Assert.Equal(UpdateResult.ReleasesPage, advice.OpenUrl);
    }

    [Fact]
    public void A_two_component_release_can_be_described_without_throwing()
    {
        // Version.ToString(3) refuses a version with fewer than three components, and a release
        // tagged v2.0 is an ordinary thing for a person to publish.
        var advice = new UpdateResult(GitHubReleaseCheck.ParseVersion("v2.0"), null).Advise(new Version(1, 0, 0));

        Assert.Contains("v2.0.0 is available", advice.Message);
    }
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

        // No body, no query, and exactly the two headers the request needs — asserted as the whole
        // set, so an Authorization or an install identifier added later fails here rather than
        // quietly turning a version check into a report.
        Assert.Null(handler.LastRequest!.Content);
        Assert.Empty(handler.LastRequest.RequestUri!.Query);
        Assert.Equal(["Accept", "User-Agent"], handler.LastRequest.Headers.Select(h => h.Key).Order());
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
    [InlineData("""{"tag_name":12}""")]             // not a string
    [InlineData("""{"tag_name":null}""")]
    [InlineData("""{"tag_name":{"v":"1.0.0"}}""")]
    [InlineData("""{"tag_name":["v1.0.0"]}""")]
    public async Task An_unreadable_answer_means_not_knowing(string body)
        => Assert.Equal(UpdateResult.Unknown, await Returning(HttpStatusCode.OK, body).LatestAsync());

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("\\\\evil.example\\share\\Termyn-Update.exe")]
    [InlineData("http://github.com/tridian-tn/termyn/releases/tag/v9")]  // not https
    [InlineData("https://evil.example/termyn")]                          // not the project's host
    [InlineData("https://github.com/someone-else/termyn/releases/tag/v9")]        // not this project
    [InlineData("https://github.com/x/y/releases/download/v9/Termyn-setup.exe")]  // an arbitrary binary
    [InlineData("https://github.com/login/oauth/authorize?client_id=x&scope=repo")]
    [InlineData("https://github.com/tridian-tn/termyn")]        // the repo itself, not a page under it
    public async Task A_release_page_we_would_not_open_is_dropped(string url)
    {
        // This ends at ShellExecute, which runs a UNC path or a file: URL as readily as it opens a
        // page — so anyone able to tamper with the response would be one dialog from launching a
        // program of their choosing.
        // Escaped, so the body stays valid JSON and the test is about the URL rather than the parse.
        var body = $$"""{"tag_name":"v9.0.0","html_url":"{{url.Replace("\\", "\\\\")}}"}""";

        var result = await Returning(HttpStatusCode.OK, body).LatestAsync();

        Assert.Equal(new Version(9, 0, 0), result.Latest);   // the version is still worth knowing
        Assert.Null(result.ReleaseUrl);
    }

    [Fact]
    public async Task A_body_larger_than_we_will_read_means_not_knowing()
    {
        // HttpClient's own buffer limit doesn't apply to a response read as a stream, so the cap
        // has to be enforced on the read.
        var huge = $$"""{"tag_name":"v9.0.0","note":"{{new string('x', 2 * 1024 * 1024)}}"}""";

        Assert.Equal(UpdateResult.Unknown, await Returning(HttpStatusCode.OK, huge).LatestAsync());
    }

    [Fact]
    public async Task A_body_that_stops_halfway_means_not_knowing()
    {
        var check = new GitHubReleaseCheck(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) })));

        Assert.Equal(UpdateResult.Unknown, await check.LatestAsync());
    }

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

    [Fact]
    public async Task The_caller_s_cancellation_reaches_the_request()
    {
        // Cancelled from inside the send, so this proves the token was actually forwarded rather
        // than merely re-read by the catch filter afterwards — a check that never aborts when the
        // window closes would otherwise look identical.
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(_ =>
        {
            cts.Cancel();
            return Resp(HttpStatusCode.OK, """{"tag_name":"v1.0.0"}""");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GitHubReleaseCheck(new HttpClient(handler)).LatestAsync(cts.Token));

        Assert.True(handler.TokenCancelledDuringSend);
    }

    [Fact]
    public async Task An_endpoint_it_was_given_is_the_one_it_asks()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, """{"tag_name":"v1.0.0"}"""));

        await new GitHubReleaseCheck(new HttpClient(handler), "https://example.test/latest").LatestAsync();

        Assert.Equal("https://example.test/latest", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("application/vnd.github+json", handler.LastRequest.Headers.Accept.ToString());
    }

    [Fact]
    public async Task A_release_the_size_GitHub_actually_sends_is_read()
    {
        // Every other success case here is a hundred bytes of hand-written JSON, so a cap lowered to
        // some plausible-sounding "8KB is plenty" would pass the whole suite while turning the update
        // check into a permanent "couldn't reach it" on every real machine.
        var notes = new string('x', 40 * 1024);
        var assets = string.Join(",", Enumerable.Range(0, 6).Select(i =>
            $$$"""{"name":"Termyn-1.4.0-setup-{{{i}}}.exe","size":3000000,"uploader":{"login":"tridian-tn","id":{{{i}}}}}"""));
        var body = $$"""{"tag_name":"v1.4.0","html_url":"https://github.com/tridian-tn/termyn/releases/tag/v1.4.0","body":"{{notes}}","assets":[{{assets}}]}""";

        Assert.True(body.Length > 40 * 1024, "the point of this test is a realistically large body");

        var result = await Returning(HttpStatusCode.OK, body).LatestAsync();

        Assert.Equal(new Version(1, 4, 0), result.Latest);
    }

    [Theory]
    [InlineData(1024 * 1024, true)]
    [InlineData(1024 * 1024 + 1, false)]
    public async Task A_body_at_the_cap_is_read_and_one_byte_past_it_is_not(int total, bool readable)
    {
        // The only other cap test is two orders of magnitude past the boundary, so it says nothing
        // about an off-by-one here.
        const string head = @"{""tag_name"":""v9.0.0"",""note"":""";
        var body = head + new string('x', total - head.Length - 2) + @"""}";
        Assert.Equal(total, body.Length);

        var result = await Returning(HttpStatusCode.OK, body).LatestAsync();

        Assert.Equal(readable ? new Version(9, 0, 0) : null, result.Latest);
    }

    [Fact]
    public async Task A_server_that_lies_about_the_length_is_still_capped()
    {
        // The cap is enforced on what arrives, not on what the sender claims — which is the whole
        // reason it isn't a Content-Length check, and nothing else here would notice it becoming one.
        var huge = $$"""{"tag_name":"v9.0.0","note":"{{new string('x', 2 * 1024 * 1024)}}"}""";
        var content = new StringContent(huge, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = 10;

        var check = new GitHubReleaseCheck(new HttpClient(new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));

        Assert.Equal(UpdateResult.Unknown, await check.LatestAsync());
    }

    [Fact]
    public async Task A_body_that_never_finishes_arriving_ends_anyway()
    {
        // HttpClient.Timeout stops covering things once the headers are in, so without a deadline of
        // its own this waits for as long as the server cares to hold the socket — measured at 45 and
        // 63 seconds against a three-second client timeout before the fix.
        var check = new GitHubReleaseCheck(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream()),
            })),
            deadline: TimeSpan.FromMilliseconds(250));

        // Raced against a clock rather than simply awaited: without the deadline this never returns
        // at all, and a test that hangs forever is a worse thing to leave in CI than one that fails.
        var running = check.LatestAsync();
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(running, finished);
        Assert.Equal(UpdateResult.Unknown, await running);
    }

    [Fact]
    public async Task Cancelling_while_the_body_is_arriving_is_not_swallowed()
    {
        // The other cancellation test proves the token reaches SendAsync. This one is about the read
        // that actually takes time — and about the check not outliving the window that asked for it.
        using var cts = new CancellationTokenSource();
        var check = new GitHubReleaseCheck(new HttpClient(new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(cts)) })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.LatestAsync(cts.Token));
    }

    private static GitHubReleaseCheck Returning(HttpStatusCode status, string json)
        => new(new HttpClient(new StubHandler(_ => Resp(status, json))));

    /// <summary>A body that starts arriving and then doesn't finish — a stalled server, or a dead link.</summary>
    private sealed class StallingStream : Stream
    {
        private readonly CancellationTokenSource? _cancelOnFirstRead;
        private bool _served;

        public StallingStream(CancellationTokenSource? cancelOnFirstRead = null)
            => _cancelOnFirstRead = cancelOnFirstRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_served)
            {
                _served = true;
                _cancelOnFirstRead?.Cancel();

                // Enough to be under way, so this is a stall mid-body rather than a refusal.
                // Trimmed to the buffer, which the JSON reader can offer a byte at a time.
                var opening = Encoding.UTF8.GetBytes(@"{""tag_name"":").AsSpan();
                var served = Math.Min(opening.Length, buffer.Length);
                opening[..served].CopyTo(buffer.Span);
                return served;
            }

            // Never completes of its own accord: only the deadline or the caller's cancel ends it.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }
    }

    private static HttpResponseMessage Resp(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>Whether the token handed to the request had been cancelled by the time it ran.</summary>
        public bool TokenCancelledDuringSend { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = _responder(request);
            TokenCancelledDuringSend = cancellationToken.IsCancellationRequested;
            return Task.FromResult(response);
        }
    }

    /// <summary>A body that dies partway through, which is the usual network failure after headers.</summary>
    private sealed class FailingStream : Stream
    {
        private int _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served > 0)
                throw new IOException("the connection went away");

            _served = 1;
            buffer[offset] = (byte)'{';
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
