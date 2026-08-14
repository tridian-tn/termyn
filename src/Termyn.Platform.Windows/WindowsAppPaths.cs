using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>Windows implementation of <see cref="IAppPaths"/> under %APPDATA% / %LOCALAPPDATA%.</summary>
public sealed class WindowsAppPaths : IAppPaths
{
    private const string AppFolder = "Termyn";

    public WindowsAppPaths()
    {
        ConfigDirectory = EnsureDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder));
        CacheDirectory = EnsureDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder));
        LogDirectory = EnsureDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder, "logs"));

        // Not created up front, unlike the others: an account that never opens an attachment should
        // not have a folder sitting there implying it downloaded something.
        AttachmentDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder, "attachments");
    }

    public string ConfigDirectory { get; }

    public string CacheDirectory { get; }

    public string LogDirectory { get; }

    public string AttachmentDirectory { get; }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
