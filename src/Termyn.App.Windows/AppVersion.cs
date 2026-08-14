using System.Diagnostics;
using System.Reflection;
using Termyn.Core;
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
            ? UpdateResult.ThreeParts(version)
            : new Version(0, 0, 0);

    /// <summary>The version as a release tag is written, which is the number with a leading v.</summary>
    public static string Tag => UpdateResult.Tag(Current);

    /// <summary>Where the app is running from, for the about box.</summary>
    public static string Location => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Opens a link in the user's browser.
    /// </summary>
    /// <remarks>
    /// Checked again here even though callers are expected to pass something already vetted,
    /// because this is the one place that reaches ShellExecute. One of the callers passes a URL
    /// that came off the network, and a single missed check there would be the difference between
    /// opening a page and running a program.
    /// </remarks>
    /// <returns>False when the link wasn't one worth opening</returns>
    public static bool OpenLink(string? url)
    {
        if (Links.Openable(url) is not { } safe)
            return false;

        Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true });
        return true;
    }

    /// <summary>
    /// Opens a link out of the user's own description, which may point anywhere on the web.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="OpenLink"/> because the question is a different one: that asks whether
    /// a link is one of Termyn's own, and this asks only whether it is a web address at all. What
    /// the two have in common is that neither hands the shell anything it was not asked to check.
    /// </remarks>
    /// <param name="url">A link written in a task's description</param>
    /// <returns>False when it isn't a web address, so the caller can say so</returns>
    public static bool OpenExternal(string? url)
    {
        if (Links.External(url) is not { } safe)
            return false;

        Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true });
        return true;
    }

    /// <summary>
    /// Hands a downloaded attachment to whatever the desktop opens that kind of file with.
    /// </summary>
    /// <remarks>
    /// Only ever called with a path this app wrote into its own cache, which is what makes shell
    /// execution acceptable here: the name came from an account, but the path did not — the cache
    /// keys files by a hash of their url and allows an extension only after checking it.
    /// </remarks>
    /// <param name="path">A file in the attachment cache</param>
    /// <returns>False when there is no such file, so the caller can say so</returns>
    public static bool OpenFile(string path)
    {
        if (!File.Exists(path))
            return false;

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;
    }
}
