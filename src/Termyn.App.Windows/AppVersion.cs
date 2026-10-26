using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    /// Opens one of the app's own folders in Explorer, for the about box's diagnostics.
    /// </summary>
    /// <param name="path">A folder the app named itself, never one off the network</param>
    /// <returns>False when there's no such folder, so the caller can say so</returns>
    public static bool OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            return false;

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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

    /// <summary>
    /// Opens Explorer on the folder a file's in, with the file selected.
    /// </summary>
    /// <remarks>
    /// Asked of the shell rather than run as <c>explorer /select,</c>: the shell reuses a window
    /// that's already open on the folder, so saving three files one after another doesn't leave
    /// three windows behind. Explorer's only started by hand when the shell won't do it.
    ///
    /// On a thread of its own, since the shell call can hold whoever makes it for a second or more
    /// while Explorer starts or reaches the folder, and the window shouldn't stop drawing for that.
    /// The thread's single-threaded, as the shell's objects need it to be.
    /// </remarks>
    /// <param name="path">A file this app has just saved</param>
    /// <returns>False when there's no such file or Explorer couldn't be got to show it, so the caller can say so</returns>
    public static Task<bool> ShowInFolder(string path)
    {
        if (!File.Exists(path))
            return Task.FromResult(false);

        var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => shown.SetResult(Show(path))) { IsBackground = true, Name = "Show in folder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return shown.Task;
    }

    /// <summary>Shows a file in its folder, on whichever thread it's called from.</summary>
    /// <param name="path">A file that's there</param>
    /// <returns>False when neither the shell nor Explorer could be got to show it</returns>
    private static bool Show(string path)
    {
        var item = ILCreateFromPathW(path);
        try
        {
            // No items, and the file's own id where the folder's would go, is how the shell is
            // asked to open the file's folder and select the file in it.
            if (item != IntPtr.Zero && SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0) == 0)
                return true;
        }
        finally
        {
            ILFree(item);
        }

        try
        {
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Not thrown on: this runs on a thread of its own, where an exception would end the
            // process, and the file's been saved either way.
            return false;
        }
    }

    // DllImport rather than LibraryImport, as elsewhere here: the generated marshalling for the
    // latter needs unsafe code, which is a lot to turn on for three calls.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(string path);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(IntPtr item);
}
