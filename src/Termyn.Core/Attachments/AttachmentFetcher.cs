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
        if (Unfetchable(attachment) is { } refused)
            return refused;

        if (_cache.Find(attachment.FileUrl, attachment.FileName) is { } held)
            return new FetchResult(FetchOutcome.Ready, held);

        if (_secrets.GetToken() is not { } token)
            return SignedOut(attachment);

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
        catch (Exception ex) when (Failure(attachment, ex, $"{attachment.FileName} couldn't be written to the download folder.") is { } failed)
        {
            _cache.Abandon(path);
            return failed;
        }
    }

    /// <summary>
    /// Puts a copy of the file in a folder, under the name it has on the account.
    /// </summary>
    /// <remarks>
    /// A file that's already in the cache is copied out of it, offline included. One that isn't is
    /// downloaded straight into the folder rather than through the cache: going through it kept a
    /// second copy, and one bigger than the cap had the sweep clear out every other cached file to
    /// make room for something the user had just put somewhere of their own.
    ///
    /// Nothing in the folder is ever written over. A name that's taken gives way to "name (1).ext",
    /// then "(2)", the way a browser numbers a second download of the same thing.
    ///
    /// Run on the pool from the start. Making the folder and the file are blocking calls, and a
    /// Downloads folder on a network share that's gone away can hold one for as long as Windows
    /// takes to give up on it. Progress still reaches whoever's showing it, since a
    /// <see cref="Progress{T}"/> reports through the context it was made on.
    /// </remarks>
    /// <param name="attachment">The file wanted</param>
    /// <param name="folder">Where the copy goes, made if it isn't there</param>
    /// <param name="progress">Told the running byte count while it's downloaded, for a UI that has to show the wait</param>
    /// <param name="ct">Cancelled when the user calls the download off</param>
    /// <returns>Ready with the copy's path, or what stopped it</returns>
    public Task<FetchResult> SaveAsync(FileAttachment attachment, string folder, IProgress<long>? progress = null, CancellationToken ct = default)
        => Task.Run(() => SaveHereAsync(attachment, folder, progress, ct));

    /// <summary>The body of <see cref="SaveAsync"/>, on whichever thread it was started on.</summary>
    private async Task<FetchResult> SaveHereAsync(FileAttachment attachment, string folder, IProgress<long>? progress, CancellationToken ct)
    {
        if (Unfetchable(attachment) is { } refused)
            return refused;

        if (_cache.Find(attachment.FileUrl, attachment.FileName) is { } held)
            return await CopyOutAsync(attachment, held, folder, ct);

        if (_secrets.GetToken() is not { } token)
            return SignedOut(attachment);

        return await WriteOutAsync(attachment, folder, target => _api.DownloadAsync(token, attachment.FileUrl, target, progress, ct));
    }

    /// <summary>
    /// Copies a file from the cache into a folder.
    /// </summary>
    /// <param name="attachment">The file being saved</param>
    /// <param name="held">Its copy in the cache</param>
    /// <param name="folder">Where the copy goes</param>
    /// <param name="ct">Cancelled when the user calls the save off</param>
    /// <returns>Ready with the copy's path, or what stopped it</returns>
    private static async Task<FetchResult> CopyOutAsync(FileAttachment attachment, string held, string folder, CancellationToken ct)
    {
        FileStream source;
        try
        {
            // Shared for writing as well as reading: the cached file may be open in whatever the
            // user opened it with, and reading alongside that is all this needs.
            source = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Said apart from a folder that won't take the copy. Here it's the cache's copy that's
            // at fault, and blaming Downloads would send the user looking in the wrong place.
            return new FetchResult(
                FetchOutcome.Failed,
                Message: $"Termyn's downloaded copy of {attachment.FileName} couldn't be read. It may be open in another program.");
        }

        await using (source)
            return await WriteOutAsync(attachment, folder, target => source.CopyToAsync(target, ct));
    }

    /// <summary>
    /// Creates the first free form of a file's name in a folder and writes the file into it.
    /// </summary>
    /// <param name="attachment">The file being saved, for its name</param>
    /// <param name="folder">Where it goes, made if it isn't there</param>
    /// <param name="write">Fills the new file</param>
    /// <returns>Ready with the new file's path, or what stopped it</returns>
    private static async Task<FetchResult> WriteOutAsync(FileAttachment attachment, string folder, Func<Stream, Task> write)
    {
        string? path = null;

        try
        {
            Directory.CreateDirectory(folder);

            var (target, created) = CreateFree(folder, DownloadName.Safe(attachment.FileName));
            path = created;

            await using (target)
                await write(target);

            return new FetchResult(FetchOutcome.Ready, path);
        }
        catch (Exception ex) when (Failure(attachment, ex, $"{attachment.FileName} couldn't be saved to {folder}.") is { } failed)
        {
            // A file that was called off or cut short isn't the file, and mustn't sit in the folder
            // looking as though it is.
            if (path is not null)
                TryDelete(path);

            return failed;
        }
    }

    /// <summary>The answer for a file there's nothing to fetch for yet, or null when there is.</summary>
    /// <param name="attachment">The file wanted</param>
    /// <returns>Pending or Missing, or null for a file that can be fetched</returns>
    private static FetchResult? Unfetchable(FileAttachment attachment)
    {
        if (attachment.Pending)
            return new FetchResult(FetchOutcome.Pending, Message: $"{attachment.FileName} is still being processed by Todoist.");

        if (!attachment.CanFetch)
            return new FetchResult(FetchOutcome.Missing, Message: $"{attachment.FileName} has no file to fetch.");

        return null;
    }

    private static FetchResult SignedOut(FileAttachment attachment)
        => new(FetchOutcome.Offline, Message: $"{attachment.FileName} hasn't been downloaded, and Termyn isn't signed in.");

    /// <summary>
    /// What a transfer that threw comes to, or null for anything a transfer isn't expected to throw.
    /// </summary>
    /// <param name="attachment">The file being transferred</param>
    /// <param name="ex">What it threw</param>
    /// <param name="unwritable">What to say when the file couldn't be written where it was going</param>
    /// <returns>The answer to give, or null to let the exception go on up</returns>
    private static FetchResult? Failure(FileAttachment attachment, Exception ex, string unwritable) => ex switch
    {
        OperationCanceledException => new FetchResult(FetchOutcome.Cancelled),

        // Told apart deliberately. Nothing answering is the ordinary offline case, and it comes back
        // on its own when the connection does. A server that answered and said no is a different
        // thing to tell the user, and "you're offline" would be untrue.
        TodoistNetworkException { Unreachable: true } => new FetchResult(
            FetchOutcome.Offline,
            Message: $"{attachment.FileName} hasn't been downloaded, and Todoist can't be reached. It'll be there to try again when you're back online."),
        TodoistNetworkException refused => new FetchResult(
            FetchOutcome.Failed,
            Message: $"Todoist wouldn't hand over {attachment.FileName}. {refused.Message}"),

        TodoistAuthException => new FetchResult(FetchOutcome.Failed, Message: "Todoist rejected the API token."),
        IOException or UnauthorizedAccessException => new FetchResult(FetchOutcome.Failed, Message: unwritable),
        _ => null,
    };

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
