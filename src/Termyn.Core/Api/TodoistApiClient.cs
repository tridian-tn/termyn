using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Termyn.Core.Api;

/// <summary>
/// Thin client over the Todoist unified API v1 sync endpoint. Resources are returned as raw
/// <see cref="JsonObject"/>s so nothing is dropped on the way in. The bearer token is supplied per
/// call so first-run validation can check a token before it is persisted.
/// </summary>
/// <remarks>
/// Every call bounds itself, so the <see cref="HttpClient"/> is expected to have no timeout of its
/// own (<see cref="TodoistHttp.CreateClient"/>). One there would span sending a request's body as
/// well, and cut off any upload that took longer than it.
///
/// A call the deadline ends comes out as a <see cref="TodoistNetworkException"/> that timed out,
/// wherever it was when it ended. Only the caller's own cancellation comes out as a cancellation.
/// </remarks>
public sealed class TodoistApiClient : ITodoistApi
{
    private const string SyncUrl = "https://api.todoist.com/api/v1/sync";
    private const string CompletedUrl = "https://api.todoist.com/api/v1/tasks/completed/by_completion_date";
    private const string UploadUrl = "https://api.todoist.com/api/v1/uploads";

    private readonly HttpClient _http;
    private readonly TimeSpan _deadline;
    private readonly TimeSpan _idle;

    /// <param name="deadline">How long one call gets. Shortened by tests that stall a body</param>
    /// <param name="idle">
    /// How long a file transfer may go without a byte moving. Shortened by tests that stall one
    /// </param>
    public TodoistApiClient(HttpClient http, TimeSpan? deadline = null, TimeSpan? idle = null)
    {
        _http = http;
        _deadline = deadline ?? DefaultDeadline;
        _idle = idle ?? DefaultIdle;
    }

    /// <summary>
    /// How long one call gets, headers and body together.
    /// </summary>
    /// <remarks>
    /// Deliberately far longer than any sync should take. It isn't a performance limit — it's there
    /// so a stalled response ends at all, and a first sync on a big account over a slow link is
    /// genuinely slow, so cutting it fine would break something that works today.
    /// </remarks>
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long an ordinary call may wait for the answer to start.
    /// </summary>
    /// <remarks>
    /// What <see cref="HttpClient.Timeout"/> used to cover, kept so a connection that's gone quiet
    /// is noticed in seconds rather than at the end of the deadline. Not for a file transfer, which
    /// sends or receives its bytes on a stall timer instead.
    /// </remarks>
    private static readonly TimeSpan HeadersDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a file transfer may go without a byte moving before it's given up.
    /// </summary>
    /// <remarks>
    /// Rather than a deadline on the whole transfer: a hundred-megabyte file over a slow link takes
    /// as long as it takes and is fine, and only one that has stopped moving is stuck.
    /// </remarks>
    private static readonly TimeSpan DefaultIdle = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A deadline for one call, linked to the caller's token so a cancel still gets through.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpClient.Timeout"/> stops applying the moment the response headers arrive,
    /// because reading the body is our own work from there. Every call here reads its body as a
    /// stream, so without this a server that sends a header and then goes quiet holds the read, the
    /// connection and the task for as long as it cares to.
    /// </remarks>
    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(_deadline);
        return source;
    }

    /// <summary>
    /// What a cancellation that wasn't the caller's comes to: the call timed out.
    /// </summary>
    /// <remarks>
    /// The same whether it fired before the headers or halfway through the body. A caller treats a
    /// timeout as the network failing and tries again; a bare cancellation it would take for its
    /// own, and stop.
    /// </remarks>
    private static TodoistNetworkException TimedOut(OperationCanceledException ex)
        => new(Strings.RequestTimedOut, ex);

    /// <inheritdoc />
    /// <remarks>
    /// The id is what tells a reconnect whether the cache it would carry on with is this account's.
    /// An answer cut off or garbled on the way is a failure to try again, like any other: read as
    /// "accepted, whose unknown", it would tell somebody pasting their own regenerated token that
    /// it was another account's, and offer to wipe what they'd been waiting to send. An answer
    /// that reads but names no user still accepts the token, with no id.
    /// </remarks>
    public async Task<TokenProbe> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var deadline = Deadline(ct);
            using var req = SyncRequest(token, "*", ["user"], []);
            using var resp = await SendRawAsync(req, deadline.Token);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return TokenProbe.Rejected;
            EnsureReachable(resp);

            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(deadline.Token);
                var root = await JsonNode.ParseAsync(stream, cancellationToken: deadline.Token) as JsonObject;
                return new TokenProbe(true, Model.Projections.ToUserId(root?["user"] as JsonObject));
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
            {
                throw new TodoistNetworkException(Strings.UnreadableTokenCheck, ex);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(ex);
        }
    }

    /// <inheritdoc />
    public async Task<SyncResponse> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct = default)
    {
        try
        {
            using var deadline = Deadline(ct);
            using var req = SyncRequest(token, syncToken, resourceTypes, commands);
            using var resp = await SendRawAsync(req, deadline.Token);

            // A 403 counts here as well as a 401. The sync is the whole account rather than one thing in
            // it, so there's nothing narrower for it to be about than the token.
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TodoistAuthException(Strings.TokenRejected);
            EnsureReachable(resp);

            // Parse straight off the response stream: buffering the whole body as a string first would
            // roughly double peak memory on a large sync.
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(deadline.Token);
                if (await JsonNode.ParseAsync(stream, cancellationToken: deadline.Token) is not JsonObject root)
                    throw new TodoistNetworkException(Strings.UnexpectedSyncResponse);
                return Parse(root, resourceTypes);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
            {
                // A reachable server that answered with unusable content, or a body that failed midway:
                // treat like any other transient failure so the caller retries and keeps its last good state.
                throw new TodoistNetworkException(Strings.UnreadableSyncResponse, ex);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(ex);
        }
    }

    /// <inheritdoc />
    public async Task<CompletedPage> GetCompletedAsync(string token, CompletedQuery query, CancellationToken ct = default)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            // Both bounds are required, and the endpoint reads them as UTC instants.
            new("since", Instant(query.Since)),
            new("until", Instant(query.Until)),
            new("limit", query.Limit.ToString()),
        };

        if (query.Cursor is { Length: > 0 } cursor)
            parameters.Add(new("cursor", cursor));

        var url = CompletedUrl + "?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var deadline = Deadline(ct);
            using var resp = await SendRawAsync(req, deadline.Token);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TodoistAuthException(Strings.TokenRejected);
            EnsureReachable(resp);

            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(deadline.Token);
                if (await JsonNode.ParseAsync(stream, cancellationToken: deadline.Token) is not JsonObject root)
                    throw new TodoistNetworkException(Strings.UnexpectedCompletedResponse);

                var items = new List<ResourceChange>();
                if (root["items"] is JsonArray array)
                {
                    foreach (var node in array)
                    {
                        if (node is not JsonObject obj || obj["id"] is not { } id)
                            continue;
                        items.Add(new ResourceChange(Model.ResourceType.Items, id.ToString(), false, obj.DeepClone().AsObject()));
                    }
                }

                var next = root["next_cursor"]?.ToString();
                return new CompletedPage(items, string.IsNullOrEmpty(next) ? null : next);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
            {
                throw new TodoistNetworkException(Strings.UnreadableCompletedResponse, ex);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(ex);
        }
    }

    // ---- Attachments -------------------------------------------------------------------------------

    /// <inheritdoc />
    /// <remarks>
    /// The url is whatever the comment carries, so the token goes only to Todoist's own hosts
    /// (<see cref="TodoistHosts.MayCarryToken"/>). A file linked from anywhere else over https is
    /// fetched without it, and anything that isn't https is refused before a request is made. A
    /// redirect can't carry the token on either: the handler drops the header when it follows one.
    ///
    /// Bounded by a stall timer rather than a deadline, so a large file on a slow link finishes.
    /// </remarks>
    public async Task DownloadAsync(string token, string fileUrl, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new TodoistUnsafeAddressException(Strings.UnsafeFileAddress);

        var authorised = TodoistHosts.MayCarryToken(url);

        try
        {
            using var stall = new Stall(ct, _idle);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (authorised)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await SendRawAsync(req, stall.Token, waitsForHeaders: false);

            // Somebody else's server turning us away says nothing about a token it was never sent.
            if (authorised)
                RejectBadToken(resp);
            EnsureReachable(resp);

            try
            {
                await using var body = await resp.Content.ReadAsStreamAsync(stall.Token);

                // Copied a block at a time so the running total can be reported and the whole thing is
                // never resident. 80 KB is the framework's own default for CopyToAsync.
                var buffer = new byte[81920];
                long total = 0;

                while (true)
                {
                    var read = await body.ReadAsync(buffer, stall.Token);
                    if (read == 0)
                        break;

                    stall.Moved();
                    await destination.WriteAsync(buffer.AsMemory(0, read), stall.Token);

                    total += read;
                    progress?.Report(total);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new TodoistNetworkException(Strings.DownloadFailedPartWay, ex);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// On a stall timer rather than a deadline, put off by every block of the file that goes, so a
    /// large one on a slow link finishes. The wait for the answer once it has all gone gets the
    /// same allowance again.
    /// </remarks>
    public async Task<JsonObject> UploadAsync(string token, Stream content, string fileName, CancellationToken ct = default)
    {
        try
        {
            using var stall = new Stall(ct, _idle);

            using var form = new MultipartFormDataContent();

            // Two parts, and the names matter: the bytes go under "file", and the name Todoist should
            // store it as goes under "file_name" as a field of its own. Putting the bytes under
            // "file_name" reads as a form with no file part in it at all.
            var file = new StreamContent(new ReadWatcher(content, stall.Moved, () => stall.Allow(_deadline)));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", fileName);
            form.Add(new StringContent(fileName), "file_name");

            using var req = new HttpRequestMessage(HttpMethod.Post, UploadUrl) { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await SendRawAsync(req, stall.Token, waitsForHeaders: false);
            RejectBadToken(resp);
            EnsureReachable(resp);

            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(stall.Token);
                if (await JsonNode.ParseAsync(stream, cancellationToken: stall.Token) is not JsonObject root)
                    throw new TodoistNetworkException(Strings.UnexpectedUploadResponse);

                return root;
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
            {
                throw new TodoistNetworkException(Strings.UnreadableUploadResponse, ex);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteUploadAsync(string token, string fileUrl, CancellationToken ct = default)
    {
        try
        {
            using var deadline = Deadline(ct);

            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{UploadUrl}?file_url={Uri.EscapeDataString(fileUrl)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await SendRawAsync(req, deadline.Token);
            RejectBadToken(resp);

            // Already gone is the outcome asked for. Todoist answers a second delete with a not-found,
            // and treating that as a failure would leave a retry that can never succeed.
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return;

            EnsureReachable(resp);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw TimedOut(ex);
        }
    }

    /// <summary>Throws for an upload or file request whose token was turned away.</summary>
    /// <remarks>
    /// A 401 only. A 403 here is about the file — one the account may not see, or a project it has
    /// left — and reading it as a bad token would sign somebody out over a permission. It goes on to
    /// be reported as the failure it is.
    /// </remarks>
    private static void RejectBadToken(HttpResponseMessage resp)
    {
        if (resp.StatusCode is HttpStatusCode.Unauthorized)
            throw new TodoistAuthException(Strings.TokenRejected);
    }

    /// <summary>Sends a prepared request, turning transport failures into the exception callers expect.</summary>
    /// <remarks>
    /// A cancellation is left to come out as one: each caller decides whether it was its deadline or
    /// its caller that fired.
    /// </remarks>
    /// <param name="req">The request</param>
    /// <param name="ct">The call's deadline, linked to its caller</param>
    /// <param name="waitsForHeaders">
    /// Whether the answer has to start within <see cref="HeadersDeadline"/>. Not for a transfer,
    /// whose upload goes out before any answer can start
    /// </param>
    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage req, CancellationToken ct, bool waitsForHeaders = true)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (waitsForHeaders)
            headers.CancelAfter(HeadersDeadline);

        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new TodoistNetworkException(Strings.CouldNotReachTodoist, ex);
        }
    }

    /// <summary>An instant in the form the completed-items endpoint expects.</summary>
    private static string Instant(DateTimeOffset moment)
        => moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static HttpRequestMessage SyncRequest(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands)
    {
        var fields = new Dictionary<string, string>
        {
            ["sync_token"] = syncToken,
            ["resource_types"] = JsonSerializer.Serialize(resourceTypes),
        };
        if (commands.Count > 0)
            fields["commands"] = SerializeCommands(commands);

        var req = new HttpRequestMessage(HttpMethod.Post, SyncUrl) { Content = new FormUrlEncodedContent(fields) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>
    /// A cancellation that fires once nothing has moved for a while, put off each time something does.
    /// </summary>
    private sealed class Stall : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private readonly TimeSpan _idle;

        public Stall(CancellationToken caller, TimeSpan idle)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _idle = idle;
            _source.CancelAfter(idle);
        }

        public CancellationToken Token => _source.Token;

        /// <summary>Something moved, so the wait starts again.</summary>
        public void Moved() => Allow(_idle);

        /// <summary>
        /// Gives the next stretch longer than the usual wait: an upload whose last byte has gone is
        /// waiting on the server to store the file, which says nothing about the connection.
        /// </summary>
        /// <param name="wait">How long to allow from now</param>
        public void Allow(TimeSpan wait)
        {
            try
            {
                _source.CancelAfter(wait);
            }
            catch (ObjectDisposedException)
            {
                // The transfer is over; there's nothing left to put off.
            }
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>
    /// The file being uploaded, saying whenever a block of it is read to go on the wire.
    /// </summary>
    /// <remarks>
    /// Seekable when the file is, so the request still says how long it is rather than going
    /// chunked. Disposes the file with it, as the content it stands in for did.
    /// </remarks>
    private sealed class ReadWatcher(Stream inner, Action moved, Action ended) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Saw(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Saw(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>
        /// Puts the stall off. At the end of the file the wait for an answer starts, and that gets the
        /// request's whole deadline: storing a large file can take Todoist longer than a stall.
        /// </summary>
        private int Saw(int read)
        {
            if (read == 0)
                ended();
            else
                moved();

            return read;
        }
    }

    private static string SerializeCommands(IReadOnlyList<Command> commands)
    {
        var array = new JsonArray();
        foreach (var c in commands)
        {
            var o = new JsonObject
            {
                ["type"] = c.Type,
                ["uuid"] = c.Uuid,
                ["args"] = c.Args.DeepClone(),
            };
            if (c.TempId is not null)
                o["temp_id"] = c.TempId;
            array.Add(o);
        }
        return array.ToJsonString();
    }

    private static void EnsureReachable(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode)
            return;

        if (resp.StatusCode is HttpStatusCode.TooManyRequests)
            throw new TodoistRateLimitException(Strings.RateLimited, RetryAfter(resp));

        throw new TodoistNetworkException(string.Format(CultureInfo.CurrentCulture, Strings.UnexpectedHttpStatus, (int)resp.StatusCode), (int)resp.StatusCode);
    }

    /// <summary>
    /// How long <c>Retry-After</c> asks us to wait. The header may be a number of seconds or an HTTP
    /// date; a date already in the past becomes zero rather than a negative wait.
    /// </summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var header = resp.Headers.RetryAfter;
        if (header is null)
            return null;

        if (header.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static SyncResponse Parse(JsonObject root, IReadOnlyList<string> resourceTypes)
    {
        var changes = new List<ResourceChange>();
        foreach (var type in resourceTypes)
        {
            switch (root[type])
            {
                case JsonArray array:
                    foreach (var node in array)
                    {
                        if (node is not JsonObject obj || obj["id"] is not { } idNode)
                            continue;
                        changes.Add(new ResourceChange(type, idNode.ToString(), ReadFlag(obj, "is_deleted"), obj.DeepClone().AsObject()));
                    }
                    break;

                // Singletons such as "user" arrive as one object rather than a collection; key them
                // by the resource type so there is always exactly one.
                case JsonObject single:
                    changes.Add(new ResourceChange(type, type, false, single.DeepClone().AsObject()));
                    break;
            }
        }

        var status = new Dictionary<string, CommandResult>();
        if (root["sync_status"] is JsonObject ss)
        {
            foreach (var kv in ss)
                status[kv.Key] = ReadStatus(kv.Value);
        }

        var tempMap = new Dictionary<string, string>();
        if (root["temp_id_mapping"] is JsonObject tm)
            foreach (var kv in tm)
                if (kv.Value is { } value)
                    tempMap[kv.Key] = value.ToString();

        var token = root["sync_token"]?.ToString();
        return new SyncResponse
        {
            SyncToken = string.IsNullOrEmpty(token) ? null : token,
            FullSync = ReadFlag(root, "full_sync"),
            Changes = changes,
            SyncStatus = status,
            TempIdMapping = tempMap,
        };
    }

    /// <summary>
    /// One command's verdict from <c>sync_status</c>.
    /// </summary>
    /// <remarks>
    /// Usually <c>"ok"</c> or an error object. A command that acts on several things at once can
    /// answer with an object of verdicts, one per thing, and that's a success unless one of them is
    /// an error. An object naming no error at all is read the same way: refusing a write the server
    /// took would roll it back here and leave the two disagreeing.
    /// </remarks>
    private static CommandResult ReadStatus(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.ToString() == "ok":
                return new CommandResult(true, null, null);

            case JsonObject err when IsError(err):
                return new CommandResult(false, err["error_code"]?.ToString(), err["error"]?.ToString(), HttpCode(err));

            case JsonObject each:
                foreach (var (_, inner) in each)
                    if (inner is JsonObject error && IsError(error))
                        return ReadStatus(error);
                return new CommandResult(true, null, null);

            default:
                return new CommandResult(false, null, node?.ToString());
        }

        static bool IsError(JsonObject o) => o.ContainsKey("error") || o.ContainsKey("error_code");

        static int? HttpCode(JsonObject o)
            => o["http_code"] is JsonValue code && code.TryGetValue(out int value) ? value : null;
    }

    // Flags have been represented as both JSON booleans and 0/1 integers; accept either.
    private static bool ReadFlag(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v)
            return false;
        if (v.TryGetValue(out bool b))
            return b;
        return v.TryGetValue(out int i) && i != 0;
    }
}
