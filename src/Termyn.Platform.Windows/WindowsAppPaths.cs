using System.Runtime.InteropServices;
using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>Windows implementation of <see cref="IAppPaths"/> under %APPDATA% / %LOCALAPPDATA%.</summary>
public sealed class WindowsAppPaths : IAppPaths
{
    private const string AppFolder = "Termyn";

    /// <summary>FOLDERID_Downloads, the shell's name for the Downloads folder.</summary>
    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>KF_FLAG_DONT_VERIFY: answer with where the folder is set to be, there or not.</summary>
    private const uint DontVerify = 0x4000;

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

    public string DownloadsDirectory => Downloads();

    /// <summary>
    /// The user's Downloads folder, wherever they've moved it.
    /// </summary>
    /// <remarks>
    /// Asked of the shell, and asked each time, rather than built from the profile path: Downloads
    /// is one of the folders Windows lets you move, to another drive or into OneDrive, and
    /// <c>%USERPROFILE%\Downloads</c> is then a folder that isn't it.
    ///
    /// Not checked for being there, either. A Downloads folder on a drive that's been unplugged is
    /// still where the user will look for what they saved, so a save there should fail and say so
    /// rather than quietly land somewhere else.
    /// </remarks>
    /// <returns>The folder's path</returns>
    internal static string Downloads()
        => SHGetKnownFolderPath(DownloadsFolder, DontVerify, IntPtr.Zero, out var path) == 0 && !string.IsNullOrEmpty(path)
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    // DllImport rather than LibraryImport, as the hotkey's calls are: the generated marshalling for
    // the latter needs unsafe code. The string the shell allocates is freed by the marshaller.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid folder,
        uint flags,
        IntPtr token,
        out string? path);
}
