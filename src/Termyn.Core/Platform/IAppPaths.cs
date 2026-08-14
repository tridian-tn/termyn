namespace Termyn.Core.Platform;

/// <summary>Resolves per-user directories for config, cache and logs.</summary>
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
}
