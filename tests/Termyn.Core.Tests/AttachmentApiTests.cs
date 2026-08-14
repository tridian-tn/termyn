using System.Net;
using System.Text;
using Termyn.Core.Api;

namespace Termyn.Core.Tests;

/// <summary>The three calls attachments add to the client: fetch a file, send one, delete one.</summary>
public class AttachmentApiTests
{
    // ---- Downloading ------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_is_written_out_as_it_arrives_rather_than_buffered()
    {
        var bytes = Encoding.UTF8.GetBytes("the file's contents");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var client = new TodoistApiClient(new HttpClient(handler));

        using var destination = new MemoryStream();
        await client.DownloadAsync("tok", "https://files.todoist.test/a", destination);

        Assert.Equal(bytes, destination.ToArray());
    }

    [Fact]
    public async Task A_download_reports_how_far_it_has_got()
    {
        // The one place the user deliberately waits on the network, so the wait has to be showable.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[200_000]),
        });
        var client = new TodoistApiClient(new HttpClient(handler));

        var seen = new System.Collections.Concurrent.ConcurrentBag<long>();
        using var destination = new MemoryStream();
        await client.DownloadAsync("tok", "https://files.todoist.test/a", destination, new Progress<long>(seen.Add));

        // Progress is raised through the synchronization context, so the last report can land after
        // the download itself has returned. Waited for rather than assumed, which is the difference
        // between a test that means something and one that flakes on a busy agent.
        for (var i = 0; i < 100 && !seen.Contains(200_000); i++)
            await Task.Delay(10);

        Assert.Contains(200_000, seen);
        Assert.Equal(200_000, destination.Length);
    }

    [Fact]
    public async Task A_download_carries_the_token()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        var client = new TodoistApiClient(new HttpClient(handler));

        using var destination = new MemoryStream();
        await client.DownloadAsync("tok", "https://files.todoist.test/a", destination);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("tok", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task A_rejected_token_on_a_download_is_told_apart_from_being_offline()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<TodoistAuthException>(
            () => client.DownloadAsync("tok", "https://files.todoist.test/a", destination));
    }

    [Fact]
    public async Task A_download_that_fails_is_reported_as_a_network_problem()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<TodoistNetworkException>(
            () => client.DownloadAsync("tok", "https://files.todoist.test/a", destination));
    }

    // ---- Uploading --------------------------------------------------------------------------------

    [Fact]
    public async Task An_upload_sends_the_file_and_returns_what_to_attach()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"file_name":"a.pdf","file_url":"https://files.todoist.test/a","upload_state":"completed"}"""),
        });
        var client = new TodoistApiClient(new HttpClient(handler));

        using var file = new MemoryStream(Encoding.UTF8.GetBytes("the bytes"));
        var attachment = await client.UploadAsync("tok", file, "a.pdf");

        Assert.Equal("https://files.todoist.test/a", attachment["file_url"]!.ToString());
        Assert.Contains("the bytes", handler.LastBody);
        Assert.Contains("a.pdf", handler.LastBody);
    }

    [Fact]
    public async Task An_upload_puts_the_bytes_under_file_and_the_name_under_file_name()
    {
        // The part names are the whole of what makes this a file upload rather than a form with a
        // long string in it. Asserted on the wire because there is nothing else to check them
        // against short of the real endpoint — the first version of this sent the bytes under
        // "file_name" and would have been refused.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"file_url":"https://files.todoist.test/a"}"""),
        });
        var client = new TodoistApiClient(new HttpClient(handler));

        using var file = new MemoryStream(Encoding.UTF8.GetBytes("the bytes"));
        await client.UploadAsync("tok", file, "a.pdf");

        // Quotes stripped first: .NET leaves them off a header value that is already a valid token,
        // so asserting on the quoted form would be testing the framework's formatting rather than
        // the part names, which are the thing that matters.
        var body = handler.LastBody!.Replace("\"", string.Empty);

        Assert.Contains("name=file; filename=a.pdf", body);
        Assert.Contains("name=file_name", body);

        // And the bytes belong to the file part rather than to the name field.
        var filePart = body.IndexOf("name=file;", StringComparison.Ordinal);
        Assert.True(filePart >= 0, $"there is no file part: {body}");
        Assert.True(
            body.IndexOf("the bytes", StringComparison.Ordinal) > filePart,
            $"the bytes do not follow the file part's own header: {body}");
    }

    [Fact]
    public async Task A_status_from_the_server_is_told_apart_from_nothing_answering()
    {
        // One comes back on its own when the connection does; the other may not. A caller reading
        // only the exception type would tell the user the wrong thing about which.
        var answered = new TodoistApiClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        using var destination = new MemoryStream();
        var ex = await Assert.ThrowsAsync<TodoistNetworkException>(
            () => answered.DownloadAsync("tok", "https://files.todoist.test/a", destination));

        Assert.Equal(500, ex.Status);
        Assert.False(ex.Unreachable);
    }

    [Fact]
    public async Task An_upload_that_comes_back_unreadable_is_a_network_problem_rather_than_a_crash()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all"),
        })));

        using var file = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<TodoistNetworkException>(() => client.UploadAsync("tok", file, "a.pdf"));
    }

    // ---- Deleting ---------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_an_upload_names_it_by_url()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new TodoistApiClient(new HttpClient(handler));

        await client.DeleteUploadAsync("tok", "https://files.todoist.test/a b.pdf");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains(Uri.EscapeDataString("https://files.todoist.test/a b.pdf"), handler.LastRequest.RequestUri!.Query);
    }

    [Fact]
    public async Task Deleting_one_that_has_already_gone_is_the_outcome_asked_for()
    {
        // Todoist answers a second delete with a not-found. Treating that as a failure would leave a
        // retry that can never succeed.
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        await client.DeleteUploadAsync("tok", "https://files.todoist.test/a");
    }

    [Fact]
    public async Task A_refused_delete_is_still_reported()
    {
        var client = new TodoistApiClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        await Assert.ThrowsAsync<TodoistNetworkException>(
            () => client.DeleteUploadAsync("tok", "https://files.todoist.test/a"));
    }

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
