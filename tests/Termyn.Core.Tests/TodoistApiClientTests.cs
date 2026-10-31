using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Termyn.Core.Api;

namespace Termyn.Core.Tests;

public class TodoistApiClientTests
{
    [Fact]
    public async Task A_sync_body_that_never_finishes_arriving_ends_anyway()
    {
        // HttpClient.Timeout stops applying once the headers are in, so a server that sends a header
        // and then goes quiet held the read, the connection and the sync loop for as long as it
        // liked. Worse here than on the update check: this is what the app does on a timer.
        var client = new TodoistApiClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream()),
            })),
            deadline: TimeSpan.FromMilliseconds(250));

        // Raced against a clock rather than simply awaited: without the deadline it never returns at
        // all, and a test that hangs forever is a worse thing to leave in CI than one that fails.
        // As a Task, because racing it against a timer needs one and this hands back a ValueTask.
        var running = Record.ExceptionAsync(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken)).AsTask();
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Same(running, finished);

        // A timeout, the same as one before the headers: let out as a bare cancellation, the caller
        // took it for its own and the sync loop reported it as a crash rather than as being offline.
        var thrown = Assert.IsType<TodoistNetworkException>(await running);
        Assert.True(thrown.Unreachable);
        Assert.IsAssignableFrom<OperationCanceledException>(thrown.InnerException);
    }

    [Fact]
    public async Task Calling_a_sync_off_part_way_through_its_body_is_still_a_cancellation()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        })));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SyncAsync("tok", "*", ["items"], [], cts.Token));
    }

    [Fact]
    public async Task A_download_that_stops_moving_times_out()
    {
        var client = new TodoistApiClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) })),
            idle: TimeSpan.FromMilliseconds(250));

        using var destination = new MemoryStream();
        var running = Record.ExceptionAsync(() => client.DownloadAsync("tok", "https://files.todoist.com/a", destination, ct: TestContext.Current.CancellationToken)).AsTask();
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Same(running, finished);
        Assert.True(Assert.IsType<TodoistNetworkException>(await running).Unreachable);
    }

    [Fact]
    public async Task A_download_that_keeps_moving_finishes_however_long_it_takes()
    {
        // A whole-transfer deadline failed a large file on a slow link that was doing nothing wrong.
        var client = new TodoistApiClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TricklingStream(8, TimeSpan.FromMilliseconds(100))) })),
            deadline: TimeSpan.FromMilliseconds(300),
            idle: TimeSpan.FromMilliseconds(500));

        using var destination = new MemoryStream();
        await client.DownloadAsync("tok", "https://files.todoist.com/a", destination, ct: TestContext.Current.CancellationToken);

        Assert.Equal(8, destination.Length);
    }

    [Fact]
    public async Task An_upload_that_keeps_moving_finishes_however_long_it_takes()
    {
        // The handler reads the whole file before answering, the way the wire does.
        var client = new TodoistApiClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"file_url":"https://files.todoist.com/a"}""") })),
            deadline: TimeSpan.FromMilliseconds(300),
            idle: TimeSpan.FromMilliseconds(500));

        using var file = new TricklingStream(8, TimeSpan.FromMilliseconds(100));
        var attachment = await client.UploadAsync("tok", file, "a.bin", TestContext.Current.CancellationToken);

        Assert.Equal("https://files.todoist.com/a", attachment["file_url"]!.ToString());
    }

    [Fact]
    public async Task An_upload_whose_answer_never_comes_times_out()
    {
        var client = new TodoistApiClient(new HttpClient(new SilentHandler()), idle: TimeSpan.FromMilliseconds(250));

        using var file = new MemoryStream([1, 2, 3]);
        var running = Record.ExceptionAsync(() => client.UploadAsync("tok", file, "a.bin", TestContext.Current.CancellationToken)).AsTask();
        var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Same(running, finished);
        Assert.True(Assert.IsType<TodoistNetworkException>(await running).Unreachable);
    }

    [Fact]
    public void The_shared_client_leaves_the_bounds_to_each_call()
    {
        using var http = TodoistHttp.CreateClient();

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    /// <summary>A body or a file that arrives a byte at a time, steadily, for as long as it lasts.</summary>
    private sealed class TricklingStream(int bytes, TimeSpan gap) : Stream
    {
        private int _left = bytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_left == 0 || buffer.Length == 0)
                return 0;

            await Task.Delay(gap, ct).ConfigureAwait(false);
            buffer.Span[0] = 1;
            _left--;
            return 1;
        }
    }

    /// <summary>A server that takes the request and never answers it.</summary>
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>A body that starts arriving and then doesn't finish — a stalled server, or a dead link.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _served;

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

                // Enough to be under way, so this is a stall mid-body rather than a refusal.
                // Trimmed to the buffer, which the JSON reader can offer a byte at a time.
                var opening = Encoding.UTF8.GetBytes(@"{""sync_token"":").AsSpan();
                var served = Math.Min(opening.Length, buffer.Length);
                opening[..served].CopyTo(buffer.Span);
                return served;
            }

            // Never completes of its own accord: only the deadline ends it.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }
    }

    [Fact]
    public async Task Parses_changes_tombstones_status_and_temp_ids()
    {
        const string json = """
        {
          "sync_token": "next",
          "full_sync": true,
          "items": [
            { "id": "i1", "content": "A", "checked": false },
            { "id": "i2", "is_deleted": true }
          ],
          "projects": [ { "id": "p1", "name": "Work" } ],
          "sync_status": { "u-ok": "ok", "u-err": { "error_code": 15, "error": "boom" } },
          "temp_id_mapping": { "t-1": "real-1" }
        }
        """;
        var client = ClientReturning(HttpStatusCode.OK, json);

        var result = await client.SyncAsync("tok", "*", ["items", "projects"], [], TestContext.Current.CancellationToken);

        Assert.Equal("next", result.SyncToken);
        Assert.True(result.FullSync);
        Assert.Equal(3, result.Changes.Count);

        var i1 = result.Changes.Single(c => c.Id == "i1");
        Assert.False(i1.IsDeleted);
        Assert.Equal("A", i1.Json["content"]!.ToString());

        Assert.True(result.Changes.Single(c => c.Id == "i2").IsDeleted);
        Assert.Equal("items", result.Changes.Single(c => c.Id == "i1").ResourceType);
        Assert.Equal("projects", result.Changes.Single(c => c.Id == "p1").ResourceType);

        Assert.True(result.SyncStatus["u-ok"].Ok);
        Assert.False(result.SyncStatus["u-err"].Ok);
        Assert.Equal("boom", result.SyncStatus["u-err"].Error);

        Assert.Equal("real-1", result.TempIdMapping["t-1"]);
    }

    [Fact]
    public async Task Tolerates_missing_collections_and_reports_no_sync_token()
    {
        var client = ClientReturning(HttpStatusCode.OK, "{}");

        var result = await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken);

        Assert.Empty(result.Changes);
        Assert.Empty(result.SyncStatus);
        Assert.Null(result.SyncToken); // absent token must not reset the caller's position
    }

    [Fact]
    public async Task Parses_a_command_error_that_carries_only_an_error_code()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"sync_status":{"u":{"error_code":15}}}""");

        var result = await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken);

        var status = result.SyncStatus["u"];
        Assert.False(status.Ok);
        Assert.Equal("15", status.ErrorCode);
        Assert.Null(status.Error);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"i1":"ok","i2":"ok"}""")]
    public async Task A_verdict_that_names_no_error_is_a_success(string verdict)
    {
        // Read as a refusal, a write the server took was rolled back here and the two disagreed.
        var client = ClientReturning(HttpStatusCode.OK, """{"sync_status":{"u":""" + verdict + "}}");

        var result = await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken);

        Assert.True(result.SyncStatus["u"].Ok);
    }

    [Fact]
    public async Task A_verdict_per_thing_with_an_error_among_them_is_a_refusal()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"sync_status":{"u":{"i1":"ok","i2":{"error_code":22,"error":"Item not found","http_code":404}}}}""");

        var result = await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken);

        var status = result.SyncStatus["u"];
        Assert.False(status.Ok);
        Assert.Equal("Item not found", status.Error);
        Assert.Equal(404, status.HttpCode);
    }

    [Fact]
    public async Task A_refusal_says_what_status_the_server_gave_it()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"sync_status":{"u":{"error_code":42,"error":"Service unavailable","http_code":503}}}""");

        var result = await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken);

        Assert.Equal(503, result.SyncStatus["u"].HttpCode);
    }

    [Fact]
    public async Task Coerces_numeric_resource_ids_to_strings()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"items":[{"id":123,"content":"A"}]}""");

        var result = await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken);

        Assert.Equal("123", result.Changes.Single().Id);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    public async Task Reads_a_tombstone_flag_as_bool_or_integer(string flag)
    {
        var client = ClientReturning(HttpStatusCode.OK, $$"""{"items":[{"id":"i1","is_deleted":{{flag}}}]}""");

        var result = await client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken);

        Assert.True(result.Changes.Single().IsDeleted);
    }

    [Fact]
    public async Task An_unreadable_body_is_reported_as_a_network_failure()
    {
        var client = ClientReturning(HttpStatusCode.OK, "{ this is not json");

        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ValidateToken_returns_false_when_rejected(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
        Assert.False((await client.ValidateTokenAsync("bad", TestContext.Current.CancellationToken)).Accepted);
    }

    [Fact]
    public async Task ValidateToken_returns_true_on_success()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{ "sync_token": "x" }""");
        Assert.True((await client.ValidateTokenAsync("good", TestContext.Current.CancellationToken)).Accepted);
    }

    [Fact]
    public async Task ValidateToken_says_which_account_the_token_belongs_to()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{ "sync_token": "x", "user": { "id": "2671355", "full_name": "Someone" } }""");

        var probe = await client.ValidateTokenAsync("good", TestContext.Current.CancellationToken);

        Assert.True(probe.Accepted);
        Assert.Equal("2671355", probe.UserId);
    }

    [Fact]
    public async Task ValidateToken_treats_an_unreadable_answer_as_a_failure_to_try_again()
    {
        // Read as "accepted, whose unknown", it would tell someone pasting their own token that it
        // was another account's.
        var client = ClientReturning(HttpStatusCode.OK, "{ this is not json");

        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.ValidateTokenAsync("good", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateToken_accepts_a_token_whose_answer_names_no_user()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{ "sync_token": "x" }""");

        var probe = await client.ValidateTokenAsync("good", TestContext.Current.CancellationToken);

        Assert.True(probe.Accepted);
        Assert.Null(probe.UserId);
    }

    [Fact]
    public async Task ValidateToken_throws_network_on_server_error()
    {
        var client = ClientReturning(HttpStatusCode.InternalServerError, "{}");
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.ValidateTokenAsync("tok", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Sync_throws_auth_when_token_rejected(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
        await Assert.ThrowsAsync<TodoistAuthException>(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sync_throws_network_on_server_error()
    {
        var client = ClientReturning(HttpStatusCode.ServiceUnavailable, "{}");
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sync_wraps_transport_failure_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sync_wraps_timeout_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new TaskCanceledException("timeout"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sync_propagates_caller_cancellation()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new TaskCanceledException())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SyncAsync("tok", "*", ["items"], [], cts.Token));
    }

    [Fact]
    public async Task Sends_bearer_token_resource_types_and_commands()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, "{}"));
        var client = new TodoistApiClient(new HttpClient(handler));
        var command = new Command("item_add", "uuid-1", "temp-1", new JsonObject { ["content"] = "Hi" });

        await client.SyncAsync("secret-token", "*", ["items", "projects"], [command], TestContext.Current.CancellationToken);

        Assert.Equal("secret-token", handler.LastRequest!.Headers.Authorization!.Parameter);
        var decoded = Uri.UnescapeDataString(handler.LastBody!.Replace("+", " "));
        Assert.Contains("resource_types=[\"items\",\"projects\"]", decoded);
        Assert.Contains("\"type\":\"item_add\"", decoded);
        Assert.Contains("\"uuid\":\"uuid-1\"", decoded);
        Assert.Contains("\"temp_id\":\"temp-1\"", decoded);
        Assert.Contains("\"content\":\"Hi\"", decoded);
    }

    // ---- Rate limiting -------------------------------------------------------------------------

    [Fact]
    public async Task A_rate_limit_is_reported_as_its_own_failure_with_the_wait_the_server_asked_for()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.Add("Retry-After", "42"));

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
    }

    [Fact]
    public async Task A_rate_limit_is_an_answer_rather_than_being_offline()
    {
        // Read as unreachable, a download refused for it told the user they were offline.
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}");

        using var destination = new MemoryStream();
        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(
            () => client.DownloadAsync("tok", "https://files.todoist.com/a", destination, ct: TestContext.Current.CancellationToken));

        Assert.False(ex.Unreachable);
        Assert.Equal(429, ex.Status);
    }

    [Fact]
    public async Task A_rate_limit_with_no_advice_says_so_rather_than_guessing()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));

        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task A_retry_after_date_already_past_is_no_wait_rather_than_a_negative_one()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}",
            r => r.Headers.Add("Retry-After", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("R")));

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() => client.ValidateTokenAsync("tok", TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
    }

    [Fact]
    public async Task A_retry_after_date_in_the_future_becomes_the_wait_it_names()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}",
            r => r.Headers.Add("Retry-After", DateTimeOffset.UtcNow.AddSeconds(90).ToString("R")));

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() => client.SyncAsync("tok", "*", ["items"], [], TestContext.Current.CancellationToken));

        // A few seconds of slack: the header has one-second resolution and time passes in between.
        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(95));
    }

    [Fact]
    public async Task A_rate_limit_on_the_completed_fetch_is_reported_as_one()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}",
            r => r.Headers.Add("Retry-After", "12"));

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(12), ex.RetryAfter);
    }

    // ---- Completed tasks -----------------------------------------------------------------------

    [Fact]
    public async Task Completed_tasks_are_fetched_from_the_by_completion_date_endpoint()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, """{"items":[],"next_cursor":null}"""));
        var client = new TodoistApiClient(new HttpClient(handler));
        var since = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var until = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

        await client.GetCompletedAsync("secret-token", new CompletedQuery(since, until, Cursor: "c-1", Limit: 100), TestContext.Current.CancellationToken);

        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal("/api/v1/tasks/completed/by_completion_date", uri.AbsolutePath);
        Assert.Equal("secret-token", handler.LastRequest.Headers.Authorization!.Parameter);

        var query = Uri.UnescapeDataString(uri.Query);
        Assert.Contains("since=2026-05-01T00:00:00Z", query);
        Assert.Contains("until=2026-07-30T12:00:00Z", query);
        Assert.Contains("limit=100", query);
        Assert.Contains("cursor=c-1", query);
    }

    [Fact]
    public async Task A_first_page_asks_for_no_cursor_and_a_local_time_is_sent_as_utc()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, """{"items":[]}"""));
        var client = new TodoistApiClient(new HttpClient(handler));
        var since = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.FromHours(2));

        await client.GetCompletedAsync("tok", new CompletedQuery(since, since.AddDays(1)), TestContext.Current.CancellationToken);

        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("since=2026-05-01T07:00:00Z", query);
        Assert.DoesNotContain("cursor=", query);
    }

    [Fact]
    public async Task Completed_items_come_back_whole_with_the_cursor_for_the_next_page()
    {
        const string json = """
        {
          "items": [
            { "id": "c1", "content": "Book dentist", "checked": true, "completed_at": "2026-07-30T09:00:00Z", "unknown_field": 7 },
            { "content": "no id at all" }
          ],
          "next_cursor": "page-2"
        }
        """;
        var client = ClientReturning(HttpStatusCode.OK, json);

        var page = await client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        Assert.Equal("page-2", page.NextCursor);
        var item = Assert.Single(page.Items);
        Assert.Equal("c1", item.Id);
        Assert.Equal("items", item.ResourceType);
        Assert.False(item.IsDeleted);
        Assert.Equal(7, item.Json["unknown_field"]!.GetValue<int>()); // nothing dropped on the way in
    }

    [Fact]
    public async Task A_last_page_reports_no_cursor()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"items":[],"next_cursor":null}""");

        Assert.Null((await client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken)).NextCursor);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Completed_throws_auth_when_token_rejected(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
        await Assert.ThrowsAsync<TodoistAuthException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    public async Task Completed_reports_an_unusable_body_as_a_network_failure(string body)
    {
        var client = ClientReturning(HttpStatusCode.OK, body);
        await Assert.ThrowsAsync<TodoistNetworkException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Completed_wraps_transport_failure_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Completed_ignores_an_items_field_that_is_not_an_array()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"items":{},"next_cursor":null}""");

        var page = await client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Completed_propagates_caller_cancellation()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new TaskCanceledException())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cts.Token));
    }

    private static TodoistApiClient ClientReturning(HttpStatusCode status, string json, Action<HttpResponseMessage>? decorate = null)
        => new(new HttpClient(new StubHandler(_ =>
        {
            var resp = Resp(status, json);
            decorate?.Invoke(resp);
            return resp;
        })));

    private static HttpResponseMessage Resp(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}
