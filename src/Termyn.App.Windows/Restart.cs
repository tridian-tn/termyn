using System.Diagnostics;
using System.Globalization;

namespace Termyn.App.Windows;

/// <summary>
/// Starting Termyn again, for a change that only takes at startup: the theme of the parts Windows
/// draws.
/// </summary>
/// <remarks>
/// The new copy is told which process it's replacing and waits for that one to be gone before it
/// tries for the single-instance lock. Started any earlier, it would find the lock still held and
/// hand itself over to a window that's on its way out, leaving nothing running. The old copy lets
/// go of the lock before it starts the new one as well, but the cache can still be open in it when a
/// sync was slow to stop, and waiting for the process covers that too.
/// </remarks>
internal static class Restart
{
    /// <summary>What the new copy is passed ahead of the old one's process id.</summary>
    internal const string AfterFlag = "--after";

    /// <summary>
    /// How long the new copy waits for the old one. Well past what a normal close takes; it's only
    /// reached if the old one has hung, and starting anyway then is better than never starting.
    /// </summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>How to start the new copy.</summary>
    /// <param name="executable">The program to start, which is this one</param>
    /// <param name="replacing">The process id of the copy that's closing</param>
    /// <returns>The start info, with no window or tray flags: a restart opens the window</returns>
    internal static ProcessStartInfo StartInfo(string executable, int replacing)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        info.ArgumentList.Add(AfterFlag);
        info.ArgumentList.Add(replacing.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    /// <summary>The process this launch was started to replace, if it was.</summary>
    /// <param name="args">The command line</param>
    /// <returns>Its id, or null when this isn't a restart or the id can't be read</returns>
    internal static int? Replacing(string[] args)
    {
        var at = Array.FindIndex(args, a => a.Equals(AfterFlag, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= args.Length)
            return null;

        return int.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    /// <summary>Waits for the copy being replaced to end.</summary>
    /// <remarks>
    /// A process id can be handed on once its process has gone, so one that's now running another
    /// program isn't waited for: the copy it named has already ended.
    /// </remarks>
    /// <param name="id">The process id of the copy being replaced</param>
    /// <param name="timeout">How long to wait at most</param>
    /// <param name="program">
    /// The name of the program it should be running. Left out it's this one, which is what a real
    /// restart wants; a test names something it can start and watch end
    /// </param>
    /// <returns>False when it was still running at the end of the wait</returns>
    internal static bool WaitFor(int id, TimeSpan timeout, string? program = null)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            // Already gone.
            return true;
        }

        using (process)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                if (!process.ProcessName.Equals(program ?? self.ProcessName, StringComparison.OrdinalIgnoreCase))
                    return true;

                return process.WaitForExit(timeout);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It ended between being found and being asked about, or it belongs to someone
                // else, and so was never the copy being replaced.
                return true;
            }
        }
    }
}
