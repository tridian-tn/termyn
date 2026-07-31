using System.Diagnostics;
using System.Reflection;

namespace Termyn.App.Windows;

/// <summary>What this build calls itself, for the about box and the update check.</summary>
internal static class AppVersion
{
    /// <summary>
    /// The running version, as three numbers. Read from the assembly rather than a constant, so it
    /// is whatever the build stamped and can't disagree with the file's own properties.
    /// </summary>
    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? new Version(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build)
            : new Version(0, 0, 0);

    /// <summary>The version as a release tag is written, which is the number with a leading v.</summary>
    public static string Tag => "v" + Current.ToString(3);

    /// <summary>Where the app is running from, for the about box.</summary>
    public static string Location => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Opens a link in the user's browser. Everything Termyn opens is one of its own constants, so
    /// there is nothing here that a task's content could steer.
    /// </summary>
    public static void OpenLink(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
