using System.Net;
using System.Text;
using Termyn.Core.Api;
using Termyn.Core.Model;

namespace Termyn.Core.Tests;

public class TodoistApiClientTests
{
    [Fact]
    public async Task Materialises_items_projects_priority_completion_due_and_filters_null_ids()
    {
        const string json = """
        {
          "sync_token": "abc",
          "full_sync": true,
          "projects": [ { "id": "p1", "name": "Work", "is_inbox_project": false, "child_order": 1 } ],
          "items": [
            { "id": "i1", "content": "Alpha", "project_id": "p1", "priority": 4, "checked": false, "child_order": 2, "due": { "date": "2026-07-30", "string": "Jul 30" } },
            { "id": "i2", "content": "Done",  "project_id": "p1", "priority": 1, "checked": true,  "child_order": 1 },
            { "id": "i3", "content": "NoPrio","project_id": "p1", "child_order": 3 },
            { "content": "NoId", "project_id": "p1" }
          ]
        }
        """;
        var client = ClientReturning(HttpStatusCode.OK, json);

        var result = await client.SyncAsync("tok", "*", ["items"]);

        Assert.Equal("abc", result.SyncToken);
        Assert.True(result.FullSync);
        Assert.Equal(3, result.Items.Count); // null-id item dropped

        var i1 = result.Items.Single(i => i.Id == "i1");
        Assert.Equal(Priority.P1, i1.Priority);   // API 4 -> P1
        Assert.False(i1.Completed);
        Assert.Equal("2026-07-30", i1.DueDate);
        Assert.Equal("Jul 30", i1.DueText);

        Assert.True(result.Items.Single(i => i.Id == "i2").Completed);
        Assert.Equal(Priority.P4, result.Items.Single(i => i.Id == "i2").Priority);   // API 1 -> P4
        Assert.Equal(Priority.P4, result.Items.Single(i => i.Id == "i3").Priority);   // missing -> P4
        Assert.Null(result.Items.Single(i => i.Id == "i3").DueDate);
    }

    [Theory]
    [InlineData("""{ "projects": [ { "id": "p", "name": "In", "is_inbox_project": true } ] }""")]
    [InlineData("""{ "projects": [ { "id": "p", "name": "In", "inbox_project": true } ] }""")]
    public async Task Accepts_either_inbox_field_name(string json)
    {
        var client = ClientReturning(HttpStatusCode.OK, json);

        var result = await client.SyncAsync("tok", "*", ["projects"]);

        Assert.True(result.Projects.Single().IsInboxProject);
    }

    [Fact]
    public async Task Tolerates_missing_collections_and_defaults_sync_token()
    {
        var client = ClientReturning(HttpStatusCode.OK, "{}");

        var result = await client.SyncAsync("tok", "*", ["items"]);

        Assert.Empty(result.Items);
        Assert.Empty(result.Projects);
        Assert.Equal("*", result.SyncToken);
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
        await Assert.ThrowsAsync<TodoistAuthException>(() => client.SyncAsync("tok", "*", ["items"]));
    }

    [Fact]
    public async Task Sync_throws_network_on_server_error()
    {
        var client = ClientReturning(HttpStatusCode.ServiceUnavailable, "{}");
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"]));
    }

    [Fact]
    public async Task Sync_wraps_transport_failure_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"]));
    }

    [Fact]
    public async Task Sync_wraps_timeout_as_network()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new TaskCanceledException("timeout"))));
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.SyncAsync("tok", "*", ["items"]));
    }

    [Fact]
    public async Task Sync_propagates_caller_cancellation()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => throw new TaskCanceledException())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SyncAsync("tok", "*", ["items"], cts.Token));
    }

    [Fact]
    public async Task Sends_bearer_token_and_encoded_resource_types()
    {
        var handler = new StubHandler(_ => Resp(HttpStatusCode.OK, "{}"));
        var client = new TodoistApiClient(new HttpClient(handler));

        await client.SyncAsync("secret-token", "*", ["projects", "items"]);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", handler.LastRequest.Headers.Authorization.Parameter);

        var decoded = Uri.UnescapeDataString(handler.LastBody!.Replace("+", " "));
        Assert.Contains("resource_types=[\"projects\",\"items\"]", decoded);
        Assert.Contains("sync_token=*", decoded);
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
