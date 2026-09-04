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
        // offer to open something has to open something. Empty counts as none — otherwise the
        // dialog appears and its OK button does nothing.
        var page = ReleaseUrl is { Length: > 0 } named ? named : ReleasesPage;
        return new UpdateAdvice($"Termyn {Tag(latest)} is available. You have {Tag(running)}.", page);
    }

    /// <summary>Where releases are listed, for when a particular one names no page.</summary>
    public const string ReleasesPage = $"https://github.com/{GitHubReleaseCheck.Repository}/releases";

    /// <summary>
    /// A version cut to three components, with an absent one read as zero.
    /// </summary>
    /// <remarks>
    /// The one place this rule lives. <see cref="Version"/> stores an absent component as -1, which
    /// sorts below a real zero — so without this, a tag written <c>v1.0.0.0</c> compares as newer
    /// than the <c>1.0.0</c> that is running, and <c>v2.0</c> throws on the way to being printed.
    /// </remarks>
    public static Version ThreeParts(Version version)
        => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>A version written the way a release tag is, which is the number with a leading v.</summary>
    public static string Tag(Version version) => "v" + ThreeParts(version).ToString(3);
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
    /// <summary>
    /// The project, in the owner/name form both the API and the website use. Written once because a
    /// rename that misses one of them leaves the check quietly 404-ing, which reads as being offline.
    /// </summary>
    public const string Repository = "tridian-tn/termyn";

    /// <summary>The latest published release, excluding drafts and pre-releases.</summary>
    public const string DefaultEndpoint = $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>
    /// The most of a response we will read. The answer is a few kilobytes of JSON; anything past this
    /// is a runaway or hostile body, and the client's own buffer limit does not apply to a response
    /// read as a stream.
    /// </summary>
    private const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// How long the whole check gets, headers and body together.
    /// </summary>
    /// <remarks>
    /// Generous, because this is never on anyone's critical path — the point is that it ends, not
    /// that it ends quickly.
    /// </remarks>
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly TimeSpan _deadline;

    /// <param name="deadline">How long the whole check gets. Shortened by tests that stall a body</param>
    public GitHubReleaseCheck(HttpClient http, string? endpoint = null, TimeSpan? deadline = null)
    {
        _http = http;
        _endpoint = endpoint ?? DefaultEndpoint;
        _deadline = deadline ?? DefaultDeadline;
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

            // HttpClient.Timeout stops applying the moment the headers arrive, because reading the
            // body is our own work from there — so a server that sends a header and then dribbles
            // one byte a minute would hold this forever, under the cap the whole way. The deadline
            // covers the read as well, and links the caller's token so a cancel still gets through.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_deadline);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                                            .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateResult.Unknown;

            if (await ReadBoundedAsync(response, deadline.Token).ConfigureAwait(false) is not { } body)
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
    /// response would otherwise be one dialog away from launching a program of their choosing.
    /// <para>
    /// The path is checked as well as the host, because github.com is not the project's own host —
    /// it's shared with every account on the site, and it serves release assets. Host alone would
    /// still allow a link to someone else's signed-in-looking download of an arbitrary executable,
    /// which is most of the way back to the thing being prevented. Anything not under this
    /// project's own path is dropped, and the caller falls back to the releases page.
    /// </para>
    /// </remarks>
    public static string? SafeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        if (uri.Host is not ("github.com" or "www.github.com"))
            return null;

        // Credentials written into the address survive into AbsoluteUri and go to the browser with
        // it, where they reach the history and whatever the address bar shows. The host check above
        // turns away a link only pretending to be GitHub; this turns away one that really is GitHub
        // and carries something else along with it.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return null;

        return uri.AbsolutePath.StartsWith($"/{Repository}/", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
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

        return UpdateResult.ThreeParts(version);
    }
}
