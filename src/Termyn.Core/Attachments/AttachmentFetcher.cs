using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Platform;

namespace Termyn.Core.Attachments;

/// <summary>How asking for a file turned out.</summary>
public enum FetchOutcome
{
    /// <summary>It's on this machine and can be opened.</summary>
    Ready,

    /// <summary>Not here, and no way to fetch it. Offering to try again on reconnect is the answer.</summary>
    Offline,

    /// <summary>The server is still processing the upload, so there is nothing to fetch yet.</summary>
    Pending,

    /// <summary>The attachment names no file to fetch.</summary>
    Missing,

    /// <summary>Asked for and then called off.</summary>
    Cancelled,

    /// <summary>Reached the server and it went wrong anyway.</summary>
    Failed,
}

/// <summary>What came of asking for a file.</summary>
/// <param name="Outcome">How it went</param>
/// <param name="Path">Where the file is, when and only when <paramref name="Outcome"/> is Ready</param>
/// <param name="Message">What to tell the user, or null when there is nothing to say</param>
public sealed record FetchResult(FetchOutcome Outcome, string? Path = null, string? Message = null);

/// <summary>
/// Fetches comment attachments on request, into a cache that's swept rather than kept.
/// </summary>
/// <remarks>
/// This is the whole of the offline-first exception, and it's deliberately the only place with one.
/// Attachment metadata syncs like anything else and is always available; the bytes are never fetched
/// on sync, only when somebody asks — which is what keeps a hundred-megabyte file on a task from
/// costing anything until it's wanted.
///
/// So every outcome that isn't Ready is an ordinary answer with something to say, not a failure to
/// report. A miss offline is the expected state of most files most of the time.
/// </remarks>
public sealed class AttachmentFetcher
{
    private readonly ITodoistApi _api;
    private readonly ISecretStore _secrets;
    private readonly AttachmentCache _cache;

    public AttachmentFetcher(ITodoistApi api, ISecretStore secrets, AttachmentCache cache)
    {
        _api = api;
        _secrets = secrets;
        _cache = cache;
    }

    /// <summary>The cache this fetches into, for the settings that sweep and empty it.</summary>
    public AttachmentCache Cache => _cache;

    /// <summary>Whether the file is already here, so the UI can offer "open" rather than "download".</summary>
    public bool IsHeld(FileAttachment attachment)
        => attachment.CanFetch && _cache.Find(attachment.FileUrl, attachment.FileName) is not null;

    /// <summary>
    /// Gets the file onto this machine, from the cache when it's already here.
    /// </summary>
    /// <param name="attachment">The file wanted</param>
    /// <param name="progress">Told the running byte count, for a UI that has to show the wait</param>
    /// <param name="ct">Cancelled when the user calls the download off</param>
    public async Task<FetchResult> FetchAsync(FileAttachment attachment, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (attachment.Pending)
            return new FetchResult(FetchOutcome.Pending, Message: $"{attachment.FileName} is still being processed by Todoist.");

        if (!attachment.CanFetch)
            return new FetchResult(FetchOutcome.Missing, Message: $"{attachment.FileName} has no file to open.");

        if (_cache.Find(attachment.FileUrl, attachment.FileName) is { } held)
            return new FetchResult(FetchOutcome.Ready, held);

        if (_secrets.GetToken() is not { } token)
            return new FetchResult(FetchOutcome.Offline, Message: $"{attachment.FileName} hasn't been downloaded, and Termyn isn't signed in.");

        var (stream, path) = _cache.OpenForWrite(attachment.FileUrl, attachment.FileName);

        try
        {
            await using (stream)
                await _api.DownloadAsync(token, attachment.FileUrl, stream, progress, ct);

            _cache.Commit(path);

            // Now, rather than only on the next start: this download is exactly what may have put
            // the cache over its cap.
            _cache.Sweep();

            return new FetchResult(FetchOutcome.Ready, path);
        }
        catch (OperationCanceledException)
        {
            _cache.Abandon(path);
            return new FetchResult(FetchOutcome.Cancelled);
        }
        catch (TodoistNetworkException)
        {
            _cache.Abandon(path);
            return new FetchResult(
                FetchOutcome.Offline,
                Message: $"{attachment.FileName} hasn't been downloaded, and Todoist can't be reached. It'll be there to try again when you're back online.");
        }
        catch (TodoistAuthException)
        {
            _cache.Abandon(path);
            return new FetchResult(FetchOutcome.Failed, Message: "Todoist rejected the API token.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _cache.Abandon(path);
            return new FetchResult(FetchOutcome.Failed, Message: $"{attachment.FileName} couldn't be written to the download folder.");
        }
    }
}
