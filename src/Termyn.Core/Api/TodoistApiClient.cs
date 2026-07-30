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
public sealed class TodoistApiClient : ITodoistApi
{
    private const string SyncUrl = "https://api.todoist.com/api/v1/sync";
    private const string QuickAddUrl = "https://api.todoist.com/api/v1/tasks/quick_add";
    private const string CompletedUrl = "https://api.todoist.com/api/v1/tasks/completed/by_completion_date";

    private readonly HttpClient _http;

    public TodoistApiClient(HttpClient http) => _http = http;

    /// <inheritdoc />
    public async Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        using var resp = await SendAsync(token, "*", ["user"], [], ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return false;
        EnsureReachable(resp);
        return true;
    }

    /// <inheritdoc />
    public async Task<SyncResponse> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct = default)
    {
        using var resp = await SendAsync(token, syncToken, resourceTypes, commands, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new TodoistAuthException("Todoist rejected the API token.");
        EnsureReachable(resp);

        // Parse straight off the response stream: buffering the whole body as a string first would
        // roughly double peak memory on a large sync.
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            if (JsonNode.Parse(stream) is not JsonObject root)
                throw new TodoistNetworkException("Todoist returned an unexpected sync response.");
            return Parse(root, resourceTypes);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            // A reachable server that answered with unusable content, or a body that failed midway:
            // treat like any other transient failure so the caller retries and keeps its last good state.
            throw new TodoistNetworkException("Todoist returned an unreadable sync response.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<ResourceChange> QuickAddAsync(string token, string text, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, QuickAddUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = text }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TodoistNetworkException("Could not reach Todoist.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TodoistNetworkException("The Todoist request timed out.", ex);
        }

        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TodoistAuthException("Todoist rejected the API token.");
            EnsureReachable(resp);

            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                if (JsonNode.Parse(stream) is not JsonObject created || created["id"] is not { } id)
                    throw new TodoistNetworkException("Todoist returned an unexpected quick-add response.");
                return new ResourceChange(Model.ResourceType.Items, id.ToString(), false, created);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
            {
                throw new TodoistNetworkException("Todoist returned an unreadable quick-add response.", ex);
            }
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

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TodoistNetworkException("Could not reach Todoist.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TodoistNetworkException("The Todoist request timed out.", ex);
        }

        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TodoistAuthException("Todoist rejected the API token.");
            EnsureReachable(resp);

            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                if (JsonNode.Parse(stream) is not JsonObject root)
                    throw new TodoistNetworkException("Todoist returned an unexpected completed-tasks response.");

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
                throw new TodoistNetworkException("Todoist returned an unreadable completed-tasks response.", ex);
            }
        }
    }

    /// <summary>An instant in the form the completed-items endpoint expects.</summary>
    private static string Instant(DateTimeOffset moment)
        => moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<HttpResponseMessage> SendAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["sync_token"] = syncToken,
            ["resource_types"] = JsonSerializer.Serialize(resourceTypes),
        };
        if (commands.Count > 0)
            fields["commands"] = SerializeCommands(commands);

        using var req = new HttpRequestMessage(HttpMethod.Post, SyncUrl) { Content = new FormUrlEncodedContent(fields) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new TodoistNetworkException("Could not reach Todoist.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TodoistNetworkException("The Todoist request timed out.", ex);
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
            throw new TodoistRateLimitException("Todoist is rate-limiting this account.", RetryAfter(resp));

        throw new TodoistNetworkException($"Todoist returned HTTP {(int)resp.StatusCode}.");
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
            {
                status[kv.Key] = kv.Value switch
                {
                    JsonValue v when v.ToString() == "ok" => new CommandResult(true, null, null),
                    JsonObject err => new CommandResult(false, err["error_code"]?.ToString(), err["error"]?.ToString()),
                    _ => new CommandResult(false, null, kv.Value?.ToString()),
                };
            }
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
