using System.Globalization;
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

    /// <summary>
    /// What a download may run past the size its comment gives before it's stopped.
    /// </summary>
    /// <remarks>
    /// The size should be exact, so this is slack rather than an estimate: a tenth, and never less
    /// than this, so a small file whose size was rounded somewhere isn't refused.
    /// </remarks>
    private const long Slack = 64 * 1024;

    /// <summary>
    /// The most any download may come to, whatever its comment says.
    /// </summary>
    /// <remarks>
    /// A comment's size and url are whatever a collaborator's client wrote, so neither is a promise.
    /// Well above what any Todoist plan uploads, this only stops a stream that doesn't end.
    /// </remarks>
    private const long Ceiling = 1024L * 1024 * 1024;

    private readonly ITodoistApi _api;
    private readonly ISecretStore _secrets;
    private readonly AttachmentCache _cache;
    private readonly IDownloadMarker _marker;

    /// <param name="api">Where files are downloaded from</param>
    /// <param name="secrets">The token to download with</param>
    /// <param name="cache">Where opened files are kept</param>
    /// <param name="marker">Tags each file written as downloaded; marks nothing when null</param>
    public AttachmentFetcher(ITodoistApi api, ISecretStore secrets, AttachmentCache cache, IDownloadMarker? marker = null)
    {
        _api = api;
        _secrets = secrets;
        _cache = cache;
        _marker = marker ?? NoDownloadMarker.Instance;
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

        // Worked out before anything's opened, so whatever goes wrong from here on — the folder
        // refusing the file included — has a part-file to tidy up and comes back as an answer.
        var path = _cache.PathFor(attachment.FileUrl, attachment.FileName);
        CappedStream? capped = null;

        try
        {
            var (stream, _) = _cache.OpenForWrite(attachment.FileUrl, attachment.FileName);
            capped = new CappedStream(stream, Allowance(attachment.FileSize));

            await using (capped)
                await _api.DownloadAsync(token, attachment.FileUrl, capped, progress, ct);

            _cache.Commit(path);
            Mark(path, attachment);

            // Now, rather than only on the next start: this download is exactly what may have put
            // the cache over its cap. Sparing it, though — it's what was asked for, and a file
            // bigger than the whole cap would otherwise be gone before it could be opened.
            _cache.Sweep(spare: path);

            return new FetchResult(FetchOutcome.Ready, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _cache.Abandon(path);
            return Failure(attachment, ex, capped?.Exceeded == true, ct, $"{attachment.FileName} couldn't be written to the download folder.");
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

        return await WriteOutAsync(attachment, folder, target => _api.DownloadAsync(token, attachment.FileUrl, target, progress, ct), ct);
    }

    /// <summary>
    /// Copies a file from the cache into a folder.
    /// </summary>
    /// <param name="attachment">The file being saved</param>
    /// <param name="held">Its copy in the cache</param>
    /// <param name="folder">Where the copy goes</param>
    /// <param name="ct">Cancelled when the user calls the save off</param>
    /// <returns>Ready with the copy's path, or what stopped it</returns>
    private async Task<FetchResult> CopyOutAsync(FileAttachment attachment, string held, string folder, CancellationToken ct)
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
            return await WriteOutAsync(attachment, folder, target => source.CopyToAsync(target, ct), ct);
    }

    /// <summary>
    /// Creates the first free form of a file's name in a folder and writes the file into it.
    /// </summary>
    /// <param name="attachment">The file being saved, for its name</param>
    /// <param name="folder">Where it goes, made if it isn't there</param>
    /// <param name="write">Fills the new file</param>
    /// <param name="ct">Cancelled when the user calls the save off, to tell that apart from a timeout</param>
    /// <returns>Ready with the new file's path, or what stopped it</returns>
    private async Task<FetchResult> WriteOutAsync(FileAttachment attachment, string folder, Func<Stream, Task> write, CancellationToken ct)
    {
        string? path = null;
        CappedStream? capped = null;

        try
        {
            Directory.CreateDirectory(folder);

            var (target, created) = CreateFree(folder, DownloadName.Safe(attachment.FileName));
            path = created;
            capped = new CappedStream(target, Allowance(attachment.FileSize));

            await using (capped)
                await write(capped);

            // Marked here as well as in the cache: a copy made by reading the cached file is a new
            // file, and nothing of the cached one's mark comes with it.
            Mark(path, attachment);

            return new FetchResult(FetchOutcome.Ready, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A file that was called off or cut short isn't the file, and mustn't sit in the folder
            // looking as though it is.
            if (path is not null)
                TryDelete(path);

            return Failure(attachment, ex, capped?.Exceeded == true, ct, $"{attachment.FileName} couldn't be saved to {folder}.");
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
    /// What a transfer that threw comes to.
    /// </summary>
    /// <remarks>
    /// Everything comes to an answer, including what a transfer isn't expected to throw — a url
    /// the client can't use, say. The window waits on this from a handler nothing else is watching,
    /// so an exception let through here is one that ends the app.
    /// </remarks>
    /// <param name="attachment">The file being transferred</param>
    /// <param name="ex">What it threw</param>
    /// <param name="exceeded">Whether it was stopped for running past what the file should come to</param>
    /// <param name="ct">The caller's token, which alone says whether the user called it off</param>
    /// <param name="unwritable">What to say when the file couldn't be written where it was going</param>
    /// <returns>The answer to give</returns>
    private static FetchResult Failure(FileAttachment attachment, Exception ex, bool exceeded, CancellationToken ct, string unwritable) => ex switch
    {
        // Asked first, whatever the client made of it on the way out: it may well have wrapped the
        // refusal as a download that failed part-way.
        _ when exceeded => new FetchResult(
            FetchOutcome.Failed,
            Message: string.Format(CultureInfo.CurrentCulture, Strings.AttachmentLargerThanSaid, attachment.FileName)),

        OperationCanceledException when ct.IsCancellationRequested => new FetchResult(FetchOutcome.Cancelled),

        // Cancelled by something other than the user, which is the client's own deadline running
        // out. A request that timed out is one nothing answered, and is said the same way.
        OperationCanceledException => Unreached(attachment),

        // Told apart deliberately. Nothing answering is the ordinary offline case, and it comes back
        // on its own when the connection does. A server that answered and said no is a different
        // thing to tell the user, and "you're offline" would be untrue.
        TodoistNetworkException { Unreachable: true } => Unreached(attachment),
        TodoistNetworkException refused => new FetchResult(
            FetchOutcome.Failed,
            Message: $"Todoist wouldn't hand over {attachment.FileName}. {refused.Message}"),

        TodoistAuthException => new FetchResult(FetchOutcome.Failed, Message: "Todoist rejected the API token."),
        IOException or UnauthorizedAccessException => new FetchResult(FetchOutcome.Failed, Message: unwritable),
        _ => new FetchResult(
            FetchOutcome.Failed,
            Message: string.Format(CultureInfo.CurrentCulture, Strings.AttachmentNotFetched, attachment.FileName)),
    };

    private static FetchResult Unreached(FileAttachment attachment) => new(
        FetchOutcome.Offline,
        Message: $"{attachment.FileName} hasn't been downloaded, and Todoist can't be reached. It'll be there to try again when you're back online.");

    /// <summary>
    /// The most a file may come to, from the size its comment gives.
    /// </summary>
    /// <param name="claimed">The comment's size, or zero or less when it doesn't say</param>
    /// <returns>How many bytes to take before stopping</returns>
    internal static long Allowance(long claimed)
        => claimed > 0 ? Math.Min(claimed + Math.Max(claimed / 10, Slack), Ceiling) : Ceiling;

    /// <summary>Tags a file just written as downloaded, without letting a failure to cost the file.</summary>
    private void Mark(string path, FileAttachment attachment)
    {
        try
        {
            _marker.Mark(path, attachment.FileUrl);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A marker isn't meant to throw. One that does has still left the file there, and the
            // user asked for the file.
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
