using Foundation;
using Termyn.Core.Platform;

namespace Termyn.Platform.Mac;

/// <summary>macOS implementation of <see cref="IAppPaths"/>, in the folders a Mac app is expected to use.</summary>
public sealed class MacAppPaths : IAppPaths
{
    private const string AppFolder = "Termyn";

    public MacAppPaths()
    {
        var library = Folder(NSSearchPathDirectory.LibraryDirectory)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library");

        // The settings and the cache share Application Support, which is the one place on a Mac
        // for both. The cache isn't only a cache: it holds the queue of changes not yet sent, so it
        // can't go in Caches, which the system empties when it wants the space.
        ConfigDirectory = EnsureDir(Path.Combine(library, "Application Support", AppFolder));
        CacheDirectory = ConfigDirectory;

        // Where Console.app looks for an app's logs.
        LogDirectory = EnsureDir(Path.Combine(library, "Logs", AppFolder));

        // Caches, unlike the others: every attachment can be fetched again, so the system is welcome
        // to it. Not created up front, for the same reason as on Windows — an account that never
        // opens an attachment shouldn't have a folder implying it downloaded something.
        AttachmentDirectory = Path.Combine(library, "Caches", AppFolder, "attachments");
    }

    public string ConfigDirectory { get; }

    public string CacheDirectory { get; }

    public string LogDirectory { get; }

    public string AttachmentDirectory { get; }

    /// <summary>
    /// The user's Downloads folder.
    /// </summary>
    /// <remarks>
    /// Asked of the system each time rather than built once, as on Windows: it's the user's folder
    /// rather than Termyn's, and not checked for being there, so a save to one that's gone fails
    /// and says so rather than landing somewhere else.
    /// </remarks>
    public string DownloadsDirectory
        => Folder(NSSearchPathDirectory.DownloadsDirectory)
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// One of the user's standard folders, as the system names it.
    /// </summary>
    /// <remarks>
    /// Asked of the system rather than taken from <see cref="Environment.SpecialFolder"/>, whose
    /// answers on macOS follow the Unix conventions — <c>ApplicationData</c> is <c>~/.config</c>,
    /// which no Mac app uses.
    /// </remarks>
    /// <param name="directory">Which folder</param>
    /// <returns>The folder's path, or null if the system doesn't say</returns>
    private static string? Folder(NSSearchPathDirectory directory)
    {
        var urls = NSFileManager.DefaultManager.GetUrls(directory, NSSearchPathDomain.User);
        return urls.Length > 0 && urls[0].Path is { Length: > 0 } path ? path : null;
    }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
