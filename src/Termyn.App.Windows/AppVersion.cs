using System.Diagnostics;
using System.Reflection;
using Termyn.Core.Update;

namespace Termyn.App.Windows;

/// <summary>What this build calls itself, and the one way it opens a link.</summary>
internal static class AppVersion
{
    /// <summary>
    /// The running version, as three numbers. Read from the assembly rather than a constant, so it
    /// is whatever the build stamped and can't disagree with the file's own properties.
    /// </summary>
    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0))
            : new Version(0, 0, 0);

    /// <summary>The version as a release tag is written, which is the number with a leading v.</summary>
    public static string Tag => UpdateResult.Tag(Current);

    /// <summary>Where the app is running from, for the about box.</summary>
    public static string Location => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Opens a link in the user's browser.
    /// </summary>
    /// <remarks>
    /// The scheme is checked here even though callers are expected to pass something already
    /// vetted, because this ends in ShellExecute — which runs a UNC path or a <c>file:</c> URL as
    /// readily as it opens a page. One of the callers passes a URL that came off the network, and a
    /// single missed check there would be the difference between opening a page and running a
    /// program.
    /// </remarks>
    /// <returns>False when the link wasn't one worth opening.</returns>
    public static bool OpenLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }
}
