namespace Termyn.Core.Platform;

/// <summary>Resolves per-user directories for config, cache and logs, and the user's own Downloads.</summary>
public interface IAppPaths
{
    /// <summary>Directory for settings and the encrypted token (e.g. <c>%APPDATA%\Termyn</c>).</summary>
    string ConfigDirectory { get; }

    /// <summary>Directory for the local cache/outbox (e.g. <c>%LOCALAPPDATA%\Termyn</c>).</summary>
    string CacheDirectory { get; }

    /// <summary>Directory for rolling logs.</summary>
    string LogDirectory { get; }

    /// <summary>
    /// Directory for downloaded comment attachments (e.g. <c>%LOCALAPPDATA%\Termyn\attachments</c>).
    /// </summary>
    /// <remarks>
    /// Kept apart from the cache database so it can be swept, emptied or deleted wholesale without
    /// going near the snapshot. Nothing in it is authoritative — every file can be fetched again.
    /// </remarks>
    string AttachmentDirectory { get; }

    /// <summary>
    /// The user's Downloads folder, where a saved copy of an attachment goes.
    /// </summary>
    /// <remarks>
    /// Not Termyn's, unlike the others: nothing in it is kept or swept, and what's put there is the
    /// user's from then on.
    /// </remarks>
    string DownloadsDirectory { get; }
}
