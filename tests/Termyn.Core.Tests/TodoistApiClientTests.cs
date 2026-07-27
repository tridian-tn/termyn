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

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ValidateToken_throws_network_on_server_error(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
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

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Quick_add_throws_network_on_server_error(HttpStatusCode status)
    {
        var client = ClientReturning(status, "{}");
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

    private static TodoistApiClient ClientReturning(HttpStatusCode status, string json)
        => new(new HttpClient(new StubHandler(_ => Resp(status, json))));

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
