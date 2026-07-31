using System.Diagnostics;
using Termyn.Core.Platform;

namespace Termyn.App.Windows;

/// <summary>
/// Records how long Termyn took to become interactive, for checking the startup budget.
/// </summary>
/// <remarks>
/// Off unless <c>TERMYN_STARTUP_TRACE</c> is set, and local to the machine either way — this is a
/// line in the log directory, not telemetry. Measured from process creation rather than from the
/// first line of Main, so it counts the runtime coming up as well as Termyn's own work.
/// </remarks>
internal static class StartupTrace
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("TERMYN_STARTUP_TRACE") is { Length: > 0 };

    private static bool _reported;

    /// <summary>Notes that the window is painted and accepting input. Only the first call counts.</summary>
    public static void Interactive(IAppPaths paths, int tasks)
    {
        if (!Enabled || _reported)
            return;
        _reported = true;

        try
        {
            var elapsed = DateTime.Now - Process.GetCurrentProcess().StartTime;
            var line = string.Join('\t',
                DateTime.Now.ToString("O"),
                $"{elapsed.TotalMilliseconds:N0}ms",
                $"{tasks} tasks",
                $"{Environment.WorkingSet / (1024 * 1024)}MB working set");

            File.AppendAllText(Path.Combine(paths.LogDirectory, "startup.log"), line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A diagnostic that can't be written is not worth failing a start over.
        }
    }
}
