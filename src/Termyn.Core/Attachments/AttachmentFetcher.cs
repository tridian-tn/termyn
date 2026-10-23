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
    /// <summary>The most numbered forms of a name tried before giving up on a folder.</summary>
    private const int MostTried = 1000;

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
            // the cache over its cap. Sparing it, though — it's what was asked for, and a file
            // bigger than the whole cap would otherwise be gone before it could be opened.
            _cache.Sweep(spare: path);

            return new FetchResult(FetchOutcome.Ready, path);
        }
        catch (OperationCanceledException)
        {
            _cache.Abandon(path);
            return new FetchResult(FetchOutcome.Cancelled);
        }
        catch (TodoistNetworkException ex)
        {
            _cache.Abandon(path);

            // Told apart deliberately. Nothing answering is the ordinary offline case, and it comes
            // back on its own when the connection does. A server that answered and said no is a
            // different thing to tell the user, and "you're offline" would be untrue.
            return ex.Unreachable
                ? new FetchResult(
                    FetchOutcome.Offline,
                    Message: $"{attachment.FileName} hasn't been downloaded, and Todoist can't be reached. It'll be there to try again when you're back online.")
                : new FetchResult(
                    FetchOutcome.Failed,
                    Message: $"Todoist wouldn't hand over {attachment.FileName}. {ex.Message}");
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

    /// <summary>
    /// Gets the file onto this machine and puts a copy of it in a folder, under the name it has on
    /// the account.
    /// </summary>
    /// <remarks>
    /// Fetched through the cache, as opening is, so a file that's already here is saved without
    /// going to the server, offline included, and one saved now opens later without being fetched
    /// again.
    ///
    /// Nothing in the folder is ever written over. A name that's taken gives way to "name (1).ext",
    /// then "(2)", the way a browser numbers a second download of the same thing.
    /// </remarks>
    /// <param name="attachment">The file wanted</param>
    /// <param name="folder">Where the copy goes, made if it isn't there</param>
    /// <param name="progress">Told the running byte count while it's fetched, for a UI that has to show the wait</param>
    /// <param name="ct">Cancelled when the user calls the download off</param>
    /// <returns>Ready with the copy's path, or whatever fetching it said</returns>
    public async Task<FetchResult> SaveAsync(FileAttachment attachment, string folder, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var fetched = await FetchAsync(attachment, progress, ct);
        if (fetched is not { Outcome: FetchOutcome.Ready, Path: { } held })
            return fetched;

        try
        {
            return new FetchResult(FetchOutcome.Ready, await CopyOutAsync(held, folder, DownloadName.Safe(attachment.FileName), ct));
        }
        catch (OperationCanceledException)
        {
            return new FetchResult(FetchOutcome.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FetchResult(FetchOutcome.Failed, Message: $"{attachment.FileName} couldn't be saved to {folder}.");
        }
    }

    /// <summary>
    /// Copies a file from the cache into a folder, under the first form of its name that's free.
    /// </summary>
    /// <param name="held">The cached file</param>
    /// <param name="folder">Where the copy goes</param>
    /// <param name="name">The name to save it under, already made safe</param>
    /// <param name="ct">Cancelled when the user calls the save off</param>
    /// <returns>Where the copy was made</returns>
    private static async Task<string> CopyOutAsync(string held, string folder, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);

        // Shared for writing as well as reading: the cached file may be open in whatever the user
        // opened it with, and reading alongside that is all this needs.
        await using var source = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 81920, useAsync: true);
        var (target, path) = CreateFree(folder, name);

        try
        {
            await using (target)
                await source.CopyToAsync(target, ct);

            return path;
        }
        catch
        {
            // A copy that was called off or cut short isn't the file, and mustn't sit in the folder
            // looking as though it is.
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// Creates the first numbered form of a name that nothing in the folder has.
    /// </summary>
    /// <remarks>
    /// Created outright rather than looked for first. Checking that a name is free and then creating
    /// it leaves a moment between the two in which something else can take it, and CreateNew does
    /// both at once.
    /// </remarks>
    /// <param name="folder">Where to create it</param>
    /// <param name="name">The plain form of the name</param>
    /// <returns>The new file, open for writing, and its path</returns>
    private static (FileStream Stream, string Path) CreateFree(string folder, string name)
    {
        for (var number = 0; number < MostTried; number++)
        {
            var path = Path.Combine(folder, DownloadName.Numbered(name, number));

            try
            {
                return (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true), path);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && Path.Exists(path))
            {
                // Taken, by an earlier save of the same file or by anything else of that name, a
                // folder included. It's never written over; the next number is tried instead.
            }
        }

        throw new IOException($"Every form of {name} up to ({MostTried - 1}) is taken in {folder}.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the user to remove. Failing to tidy up isn't worth surfacing over a save
            // that has already gone wrong.
        }
    }
}
