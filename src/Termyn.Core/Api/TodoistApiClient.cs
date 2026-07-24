using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Termyn.Core.Model;

namespace Termyn.Core.Api;

/// <summary>
/// Thin client over the Todoist unified API v1 sync endpoint. The bearer token is supplied per
/// call so first-run validation can check a token before it is persisted.
/// </summary>
public sealed class TodoistApiClient : ITodoistApi
{
    private const string SyncUrl = "https://api.todoist.com/api/v1/sync";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public TodoistApiClient(HttpClient http) => _http = http;

    /// <inheritdoc />
    public async Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        using var resp = await SendAsync(token, "*", ["user"], ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return false;
        EnsureReachable(resp);
        return true;
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, CancellationToken ct = default)
    {
        using var resp = await SendAsync(token, syncToken, resourceTypes, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new TodoistAuthException("Todoist rejected the API token.");
        EnsureReachable(resp);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var dto = await JsonSerializer.DeserializeAsync<SyncResponseDto>(stream, JsonOptions, ct)
                  ?? throw new InvalidOperationException("Empty sync response from Todoist.");
        return Materialise(dto);
    }

    private async Task<HttpResponseMessage> SendAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["sync_token"] = syncToken,
            ["resource_types"] = JsonSerializer.Serialize(resourceTypes),
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, SyncUrl) { Content = content };
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

    // A reachable server that answered with a non-success status (rate limit, 5xx, …). Surfaced as a
    // network-class failure so callers show a retry path rather than crashing.
    private static void EnsureReachable(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode)
            throw new TodoistNetworkException($"Todoist returned HTTP {(int)resp.StatusCode}.");
    }

    private static SyncResult Materialise(SyncResponseDto dto)
    {
        var items = (dto.Items ?? [])
            .Where(i => i.Id is not null)
            .Select(i => new TaskItem
            {
                Id = i.Id!,
                Content = i.Content ?? string.Empty,
                ProjectId = i.ProjectId,
                ParentId = i.ParentId,
                ChildOrder = i.ChildOrder,
                Priority = PriorityMap.FromApi(i.Priority),
                Completed = i.Checked,
                DueDate = i.Due?.Date,
                DueText = i.Due?.String,
            })
            .ToList();

        var projects = (dto.Projects ?? [])
            .Where(p => p.Id is not null)
            .Select(p => new Project
            {
                Id = p.Id!,
                Name = p.Name ?? string.Empty,
                IsInboxProject = p.IsInboxProject || p.InboxProjectLegacy,
                ChildOrder = p.ChildOrder,
            })
            .ToList();

        return new SyncResult
        {
            SyncToken = dto.SyncToken ?? "*",
            FullSync = dto.FullSync,
            Items = items,
            Projects = projects,
        };
    }

    private sealed class SyncResponseDto
    {
        [JsonPropertyName("sync_token")] public string? SyncToken { get; set; }
        [JsonPropertyName("full_sync")] public bool FullSync { get; set; }
        [JsonPropertyName("items")] public List<ItemDto>? Items { get; set; }
        [JsonPropertyName("projects")] public List<ProjectDto>? Projects { get; set; }
    }

    private sealed class ItemDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("parent_id")] public string? ParentId { get; set; }
        [JsonPropertyName("child_order")] public int ChildOrder { get; set; }
        [JsonPropertyName("priority")] public int Priority { get; set; }
        [JsonPropertyName("checked")] public bool Checked { get; set; }
        [JsonPropertyName("due")] public DueDto? Due { get; set; }
    }

    private sealed class DueDto
    {
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("string")] public string? String { get; set; }
    }

    // Todoist has used both "is_inbox_project" and "inbox_project" across API versions; accept either.
    private sealed class ProjectDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("is_inbox_project")] public bool IsInboxProject { get; set; }
        [JsonPropertyName("inbox_project")] public bool InboxProjectLegacy { get; set; }
        [JsonPropertyName("child_order")] public int ChildOrder { get; set; }
    }
}
