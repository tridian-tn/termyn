using System.Text.Json;
using System.Text.Json.Nodes;

namespace Termyn.Core.Update;

/// <summary>What a check for a newer release found.</summary>
/// <param name="Latest">The newest version published, or null when that couldn't be determined.</param>
/// <param name="ReleaseUrl">Where to read about it and download it.</param>
public sealed record UpdateResult(Version? Latest, string? ReleaseUrl)
{
    /// <summary>Nothing was learned — offline, rate-limited, or an answer we couldn't read.</summary>
    public static readonly UpdateResult Unknown = new(null, null);

    /// <summary>Whether <paramref name="running"/> is behind what has been published.</summary>
    public bool IsNewerThan(Version running) => Latest is { } latest && latest > running;
}

/// <summary>
/// Asks whether a newer Termyn has been published.
/// </summary>
/// <remarks>
/// User-initiated only, and it sends nothing: no account, no token, no identifier — a plain GET for a
/// version number. v1 has no auto-update, so the most this can do is offer to open the release page.
/// </remarks>
public interface IUpdateCheck
{
    Task<UpdateResult> LatestAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the latest release from the GitHub releases API.
/// </summary>
/// <remarks>
/// Any failure is <see cref="UpdateResult.Unknown"/> rather than an exception: not knowing whether
/// there is an update is a normal answer, and it must not interrupt whatever the user was doing.
/// </remarks>
public sealed class GitHubReleaseCheck : IUpdateCheck
{
    /// <summary>The latest published release, excluding drafts and pre-releases.</summary>
    public const string DefaultEndpoint = "https://api.github.com/repos/tridian-tn/termyn/releases/latest";

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public GitHubReleaseCheck(HttpClient http, string? endpoint = null)
    {
        _http = http;
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    /// <inheritdoc />
    public async Task<UpdateResult> LatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            request.Headers.Add("Accept", "application/vnd.github+json");

            // GitHub refuses anonymous requests with no user agent. The product name and version are
            // all it carries — nothing about the machine or the account.
            request.Headers.Add("User-Agent", "Termyn");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return UpdateResult.Unknown;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            if (JsonNode.Parse(stream) is not JsonObject release)
                return UpdateResult.Unknown;

            var tag = release["tag_name"]?.ToString();
            var url = release["html_url"]?.ToString();

            return ParseVersion(tag) is { } version ? new UpdateResult(version, url) : UpdateResult.Unknown;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                      or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Not knowing is a normal outcome for something the user asked about in passing.
            return UpdateResult.Unknown;
        }
    }

    /// <summary>
    /// Reads a release tag as a version. Tags are conventionally written with a leading <c>v</c>,
    /// which is not part of the number.
    /// </summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        // A pre-release suffix — 1.2.0-beta.1 — is not something Version can read, and a build we
        // can't order against the running one is one we shouldn't be recommending.
        return trimmed.Contains('-') ? null : Version.TryParse(trimmed, out var version) ? version : null;
    }
}
