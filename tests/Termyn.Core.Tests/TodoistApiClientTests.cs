using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Termyn.Core.Api;

namespace Termyn.Core.Tests;

public class TodoistApiClientTests
{
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

        var result = await client.SyncAsync("tok", "*", ["items", "projects"], []);

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

        var result = await client.SyncAsync("tok", "*", ["items"], []);

        Assert.Empty(result.Changes);
        Assert.Empty(result.SyncStatus);
        Assert.Null(result.SyncToken); // absent token must not reset the caller's position
    }

    [Fact]
    public async Task Parses_a_command_error_that_carries_only_an_error_code()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"sync_status":{"u":{"error_code":15}}}""");

        var result = await client.SyncAsync("tok", "*", ["items"], []);

        var status = result.SyncStatus["u"];
        Assert.False(status.Ok);
        Assert.Equal("15", status.ErrorCode);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Coerces_numeric_resource_ids_to_strings()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"items":[{"id":123,"content":"A"}]}""");

        var result = await client.SyncAsync("tok", "*", ["items"], []);

        Assert.Equal("123", result.Changes.Single().Id);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    public async Task Reads_a_tombstone_flag_as_bool_or_integer(string flag)
    {
        var client = ClientReturning(HttpStatusCode.OK, $$"""{"items":[{"id":"i1","is_deleted":{{flag}}}]}""");

        var result = await client.SyncAsync("tok", "*", ["items"], []);

        Assert.True(result.Changes.Single().IsDeleted);
    }

    [Fact]
    public async Task An_unreadable_body_is_reported_as_a_network_failure()
    {
        var client = ClientReturning(HttpStatusCode.OK, "{ this is not json");

        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"], []));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ValidateToken_returns_false_when_rejected(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
        Assert.False(await client.ValidateTokenAsync("bad"));
    }

    [Fact]
    public async Task ValidateToken_returns_true_on_success()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{ "sync_token": "x" }""");
        Assert.True(await client.ValidateTokenAsync("good"));
    }

    [Fact]
    public async Task ValidateToken_throws_network_on_server_error()
    {
        var client = ClientReturning(HttpStatusCode.InternalServerError, "{}");
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.ValidateTokenAsync("tok"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Sync_throws_auth_when_token_rejected(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
        await Assert.ThrowsAsync<TodoistAuthException>(() => client.SyncAsync("tok", "*", ["items"], []));
    }

    [Fact]
    public async Task Sync_throws_network_on_server_error()
    {
        var client = ClientReturning(HttpStatusCode.ServiceUnavailable, "{}");
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"], []));
    }

    [Fact]
    public async Task Sync_wraps_transport_failure_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"], []));
    }

    [Fact]
    public async Task Sync_wraps_timeout_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new TaskCanceledException("timeout"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"], []));
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

        await client.SyncAsync("secret-token", "*", ["items", "projects"], [command]);

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

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() => client.SyncAsync("tok", "*", ["items"], []));

        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
    }

    [Fact]
    public async Task A_rate_limit_with_no_advice_says_so_rather_than_guessing()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}");

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() => client.QuickAddAsync("tok", "A"));

        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task A_retry_after_date_already_past_is_no_wait_rather_than_a_negative_one()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}",
            r => r.Headers.Add("Retry-After", DateTimeOffset.UtcNow.AddMinutes(-5).ToString("R")));

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() => client.ValidateTokenAsync("tok"));

        Assert.Equal(TimeSpan.Zero, ex.RetryAfter);
    }

    [Fact]
    public async Task A_retry_after_date_in_the_future_becomes_the_wait_it_names()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}",
            r => r.Headers.Add("Retry-After", DateTimeOffset.UtcNow.AddSeconds(90).ToString("R")));

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() => client.SyncAsync("tok", "*", ["items"], []));

        // A few seconds of slack: the header has one-second resolution and time passes in between.
        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(95));
    }

    [Fact]
    public async Task A_rate_limit_on_the_completed_fetch_is_reported_as_one()
    {
        var client = ClientReturning(HttpStatusCode.TooManyRequests, "{}",
            r => r.Headers.Add("Retry-After", "12"));

        var ex = await Assert.ThrowsAsync<TodoistRateLimitException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)));

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

        await client.GetCompletedAsync("secret-token", new CompletedQuery(since, until, Cursor: "c-1", Limit: 100));

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

        await client.GetCompletedAsync("tok", new CompletedQuery(since, since.AddDays(1)));

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

        var page = await client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

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

        Assert.Null((await client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))).NextCursor);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Completed_throws_auth_when_token_rejected(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
        await Assert.ThrowsAsync<TodoistAuthException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    public async Task Completed_reports_an_unusable_body_as_a_network_failure(string body)
    {
        var client = ClientReturning(HttpStatusCode.OK, body);
        await Assert.ThrowsAsync<TodoistNetworkException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Completed_wraps_transport_failure_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() =>
            client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Completed_ignores_an_items_field_that_is_not_an_array()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"items":{},"next_cursor":null}""");

        var page = await client.GetCompletedAsync("tok", new CompletedQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

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

    // ---- Quick add -----------------------------------------------------------------------------

    [Fact]
    public async Task Quick_add_posts_the_raw_text_with_a_bearer_token()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, """{"id":"srv1","content":"A"}"""));
        var client = new TodoistApiClient(new HttpClient(handler));

        await client.QuickAddAsync("secret-token", "Email report #Work p1");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.todoist.com/api/v1/tasks/quick_add", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("secret-token", handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.Equal("text=Email report #Work p1", Uri.UnescapeDataString(handler.LastBody!.Replace("+", " ")));
    }

    [Fact]
    public async Task Quick_add_returns_the_created_task()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"id":"srv1","content":"A","priority":4}""");

        var created = await client.QuickAddAsync("tok", "A");

        Assert.Equal("items", created.ResourceType);
        Assert.Equal("srv1", created.Id);
        Assert.False(created.IsDeleted);
        Assert.Equal("A", created.Json["content"]!.ToString());
    }

    [Fact]
    public async Task Quick_add_coerces_a_numeric_id()
    {
        var client = ClientReturning(HttpStatusCode.OK, """{"id":123,"content":"A"}""");

        Assert.Equal("123", (await client.QuickAddAsync("tok", "A")).Id);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Quick_add_throws_auth_when_rejected(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
        await Assert.ThrowsAsync<TodoistAuthException>(() => client.QuickAddAsync("tok", "A"));
    }

    [Fact]
    public async Task Quick_add_throws_network_on_server_error()
    {
        var client = ClientReturning(HttpStatusCode.InternalServerError, "{}");
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.QuickAddAsync("tok", "A"));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{"content":"A"}""")] // no id
    public async Task Quick_add_reports_an_unusable_body_as_a_network_failure(string body)
    {
        var client = ClientReturning(HttpStatusCode.OK, body);
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.QuickAddAsync("tok", "A"));
    }

    [Fact]
    public async Task Quick_add_wraps_transport_failure_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.QuickAddAsync("tok", "A"));
    }

    [Fact]
    public async Task Quick_add_propagates_caller_cancellation()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new TaskCanceledException())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QuickAddAsync("tok", "A", cts.Token));
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
