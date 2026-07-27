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
}
