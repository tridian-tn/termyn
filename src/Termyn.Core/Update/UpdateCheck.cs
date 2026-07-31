using System.Text.Json;
using System.Text.Json.Nodes;

namespace Termyn.Core.Update;

/// <summary>What a check for a newer release found.</summary>
/// <param name="Latest">The newest version published, or null when that couldn't be determined.</param>
/// <param name="ReleaseUrl">Where to read about it. Null unless it is an https link to the project.</param>
public sealed record UpdateResult(Version? Latest, string? ReleaseUrl)
{
    /// <summary>Nothing was learned — offline, rate-limited, or an answer we couldn't read.</summary>
    public static readonly UpdateResult Unknown = new(null, null);

    /// <summary>Whether <paramref name="running"/> is behind what has been published.</summary>
    public bool IsNewerThan(Version running) => Latest is { } latest && latest > running;

    /// <summary>
    /// What to tell the user, and what to open if they say yes.
    /// </summary>
    /// <remarks>
    /// Here rather than in the window because it is the part with rules in it — which version wins,
    /// how a version is written, and where to send someone when the feed named no page. The window's
    /// share is a label and a message box.
    /// </remarks>
    public UpdateAdvice Advise(Version running)
    {
        if (Latest is not { } latest)
            return new UpdateAdvice($"Couldn't reach the update check. Termyn {Tag(running)} is what's running.", null);

        if (!IsNewerThan(running))
            return new UpdateAdvice($"Termyn {Tag(running)} is the latest.", null);

        // A release with no page of its own still gets one: the releases list always exists, and an
        // offer to open something has to open something.
        return new UpdateAdvice($"Termyn {Tag(latest)} is available. You have {Tag(running)}.", ReleaseUrl ?? ReleasesPage);
    }

    /// <summary>Where releases are listed, for when a particular one names no page.</summary>
    public const string ReleasesPage = "https://github.com/tridian-tn/termyn/releases";

    /// <summary>
    /// A version written the way a release tag is. Always three numbers: <c>Version.ToString(3)</c>
    /// throws on a version carrying fewer, and versions arrive here from a tag someone typed.
    /// </summary>
    public static string Tag(Version version)
        => $"v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
}

/// <summary>What to say about an update, and what to open if the user wants it.</summary>
/// <param name="OpenUrl">Null when there is nothing to offer, so no dialog need be shown.</param>
public sealed record UpdateAdvice(string Message, string? OpenUrl);

/// <summary>
/// Asks whether a newer Termyn has been published, by reading the latest release from GitHub.
/// </summary>
/// <remarks>
/// User-initiated only, and it sends nothing: no account, no token, no identifier — a plain GET for a
/// version number. v1 has no auto-update, so the most this can do is offer to open the release page.
/// <para>
/// Any failure is <see cref="UpdateResult.Unknown"/> rather than an exception: not knowing whether
/// there is an update is a normal answer, and it must not interrupt whatever the user was doing.
/// </para>
/// </remarks>
public sealed class GitHubReleaseCheck
{
    /// <summary>The latest published release, excluding drafts and pre-releases.</summary>
    public const string DefaultEndpoint = "https://api.github.com/repos/tridian-tn/termyn/releases/latest";

    /// <summary>
    /// The most of a response we will read. The answer is a few kilobytes of JSON; anything past this
    /// is a runaway or hostile body, and the client's own buffer limit does not apply to a response
    /// read as a stream.
    /// </summary>
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public GitHubReleaseCheck(HttpClient http, string? endpoint = null)
    {
        _http = http;
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    /// <summary>Reads the latest published release.</summary>
    public async Task<UpdateResult> LatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            request.Headers.Add("Accept", "application/vnd.github+json");

            // GitHub refuses anonymous requests with no user agent. The product name is all it
            // carries — nothing about the machine, the account or even the version.
            request.Headers.Add("User-Agent", "Termyn");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                            .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateResult.Unknown;

            if (await ReadBoundedAsync(response, ct).ConfigureAwait(false) is not { } body)
                return UpdateResult.Unknown;

            if (JsonNode.Parse(body) is not JsonObject release)
                return UpdateResult.Unknown;

            var version = ParseVersion(TextOf(release, "tag_name"));
            return version is null ? UpdateResult.Unknown : new UpdateResult(version, SafeUrl(TextOf(release, "html_url")));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                      or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Not knowing is a normal outcome for something the user asked about in passing. A
            // cancellation from the caller is excluded by the filter and propagates.
            return UpdateResult.Unknown;
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxResponseBytes"/>, asynchronously and off whatever thread asked.
    /// </summary>
    /// <returns>The body, or null when the server sent more than we were willing to read.</returns>
    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Trusting Content-Length would be trusting the sender, so the cap is enforced on the read.
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>A string property, or null when it is absent or is something other than a string.</summary>
    private static string? TextOf(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>
    /// A link we are willing to hand to the shell.
    /// </summary>
    /// <remarks>
    /// This comes off the network, and opening it goes through ShellExecute — which will run a UNC
    /// path or a <c>file:</c> URL as happily as it opens a web page. Anyone able to tamper with the
    /// response would otherwise be one dialog away from launching a program of their choosing, so
    /// only https to the project's own host is accepted; anything else is dropped and the caller
    /// falls back to the releases page.
    /// </remarks>
    public static string? SafeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return uri.Host is "github.com" or "www.github.com" ? uri.AbsoluteUri : null;
    }

    /// <summary>
    /// Reads a release tag as a version. Tags are conventionally written with a leading <c>v</c>,
    /// which is not part of the number, and are normalised to three components so a two-component
    /// tag can be compared and printed like any other.
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
        if (trimmed.Contains('-') || !Version.TryParse(trimmed, out var version))
            return null;

        // Three components, always. An absent one is -1, which sorts below a real zero and makes
        // v1.0.0.0 look newer than the 1.0.0 that is running.
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
