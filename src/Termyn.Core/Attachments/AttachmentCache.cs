using System.Security.Cryptography;
using System.Text;

namespace Termyn.Core.Attachments;

/// <summary>How much downloaded material is kept, and for how long.</summary>
/// <param name="MaxBytes">The total the cache may occupy before the oldest are dropped</param>
/// <param name="MaxAge">How long an untouched file is kept</param>
public sealed record CacheLimits(long MaxBytes, TimeSpan MaxAge)
{
    /// <summary>256 MB and a fortnight — enough to reopen the same file for as long as it's in hand.</summary>
    public static readonly CacheLimits Default = new(256L * 1024 * 1024, TimeSpan.FromDays(14));
}

/// <summary>
/// Where downloaded attachments live, and the rules that stop them living there for ever.
/// </summary>
/// <remarks>
/// Nothing here is authoritative. Every file in it can be fetched again from its url, which is what
/// makes it safe to sweep at any moment and safe to delete wholesale — and why a miss is an ordinary
/// outcome rather than an error.
///
/// Files are keyed by a hash of their url rather than by their name: two comments can each carry a
/// file called "notes.pdf", and a name from an account is not a name this machine has to accept as a
/// path in any case.
/// </remarks>
public sealed class AttachmentCache
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="directory">Where downloaded files are kept</param>
    /// <param name="limits">The size and age caps to sweep against</param>
    /// <param name="now">The clock, so a test can age a file without waiting for one</param>
    public AttachmentCache(string directory, CacheLimits? limits = null, Func<DateTimeOffset>? now = null)
    {
        _directory = directory;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Limits = limits ?? CacheLimits.Default;
    }

    /// <summary>The caps this sweeps against.</summary>
    public CacheLimits Limits { get; set; }

    /// <summary>
    /// Where a file would be kept, whether or not it's there.
    /// </summary>
    /// <remarks>
    /// The original extension is carried over so the OS still knows what it is — handing a file to
    /// the default application is done by extension, and a hash with none opens nothing.
    /// </remarks>
    /// <param name="fileUrl">The attachment's url, which is its identity</param>
    /// <param name="fileName">Its name on the account, for the extension alone</param>
    public string PathFor(string fileUrl, string fileName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileUrl)))[..32].ToLowerInvariant();
        return Path.Combine(_directory, hash + SafeExtension(fileName));
    }

    /// <summary>
    /// The extension to give a cached file, or none.
    /// </summary>
    /// <remarks>
    /// From an account, so it's checked rather than trusted: anything carrying a separator, or long
    /// enough to look like a name in its own right, is dropped instead of being built into a path.
    /// </remarks>
    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        if (extension.Length is 0 or > 16)
            return string.Empty;

        foreach (var c in extension.AsSpan(1))
            if (!char.IsAsciiLetterOrDigit(c))
                return string.Empty;

        return extension;
    }

    /// <summary>The file if it's already here, or null. A miss is ordinary — it just means fetching.</summary>
    public string? Find(string fileUrl, string fileName)
    {
        var path = PathFor(fileUrl, fileName);
        if (!File.Exists(path))
            return null;

        // Reopening the same file for a week should keep it, which age from the download alone
        // wouldn't.
        Touch(path);
        return path;
    }

    /// <summary>
    /// Notes that a file has just been wanted, which is what its age is counted from.
    /// </summary>
    /// <remarks>
    /// Stamped from the cache's own clock rather than left to whatever the filesystem put on it, so
    /// that age means one thing throughout: <see cref="Sweep"/> measures against that same clock,
    /// and a file arriving stamped by the OS is being judged by a different reckoning from the one
    /// doing the judging. The two only agree while the clock happens to be the real one.
    /// </remarks>
    /// <param name="path">The file that has just been written or opened</param>
    private void Touch(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, _now().UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file is there, which is what matters. Failing to note that it was wanted only
            // means it may be swept sooner.
        }
    }

    /// <summary>Opens the file to write a download into, making the directory if it isn't there.</summary>
    /// <returns>The stream to write to, and the path it will end up at</returns>
    public (Stream Stream, string Path) OpenForWrite(string fileUrl, string fileName)
    {
        Directory.CreateDirectory(_directory);

        var path = PathFor(fileUrl, fileName);

        // Written beside and moved into place, so a download that fails or is cancelled part-way
        // can't leave a truncated file that later looks like a hit.
        //
        // Opened for real asynchronous writing: the download awaits every block, and a handle
        // without it turns each of those into blocking work on a pool thread for as long as a
        // hundred-megabyte file takes.
        var partial = path + ".part";
        var stream = new FileStream(
            partial,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        return (stream, path);
    }

    /// <summary>Puts a finished download in place of any earlier copy.</summary>
    public void Commit(string path)
    {
        var partial = path + ".part";
        if (!File.Exists(partial))
            return;

        // A move carries the part-file's own timestamps over, so without this the file arrives aged
        // by the filesystem while everything else here counts from the cache's clock.
        File.Move(partial, path, overwrite: true);
        Touch(path);
    }

    /// <summary>Throws away a download that didn't finish.</summary>
    public void Abandon(string path)
    {
        var partial = path + ".part";

        try
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It will be swept with everything else in time. A failure to tidy up is not worth
            // surfacing over a download that has already gone wrong.
        }
    }

    /// <summary>What the cache currently occupies, in bytes.</summary>
    public long Size() => Files().Sum(f => f.Length);

    /// <summary>
    /// Drops what's past either cap: anything untouched for longer than the age allows, then the
    /// least recently used until the total fits.
    /// </summary>
    /// <returns>How many files were removed</returns>
    public int Sweep()
    {
        var files = Files().OrderBy(f => f.LastAccessTimeUtc).ToList();
        var removed = 0;
        var cutoff = _now().UtcDateTime - Limits.MaxAge;
        var total = files.Sum(f => f.Length);

        foreach (var file in files)
        {
            var stale = file.LastAccessTimeUtc < cutoff;
            var over = total > Limits.MaxBytes;

            if (!stale && !over)
                break;

            var length = file.Length;
            if (!TryDelete(file))
                continue;

            total -= length;
            removed++;
        }

        return removed;
    }

    /// <summary>Empties the cache. Nothing in it is authoritative, so this loses nothing.</summary>
    /// <returns>How many files were removed</returns>
    public int Clear()
    {
        var removed = 0;
        foreach (var file in Files())
            if (TryDelete(file))
                removed++;

        return removed;
    }

    private IEnumerable<FileInfo> Files()
    {
        if (!Directory.Exists(_directory))
            return [];

        try
        {
            return new DirectoryInfo(_directory).EnumerateFiles().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Open in whatever the user opened it with, most likely. It stays and is swept next time.
            return false;
        }
    }
}
