using Microsoft.Win32;
using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>
/// Launch at login via the per-user <c>Run</c> key. Per-user rather than machine-wide, so it needs
/// no elevation and doesn't start Termyn for anyone else who signs in.
/// </summary>
public sealed class WindowsAutoStart : IAutoStartService
{
    /// <summary>Where Windows looks for per-user startup entries.</summary>
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "Termyn";

    /// <summary>
    /// Signing in is not a request to be shown a task list, so the login entry starts Termyn in the
    /// tray — where the global hotkey is live and nothing has taken over the screen.
    /// </summary>
    private const string StartupArgument = "--tray";

    private readonly string _command;
    private readonly string _key;

    public WindowsAutoStart()
        : this(Environment.ProcessPath)
    {
    }

    /// <param name="executablePath">
    /// The binary to launch. Null when the host can't say what it is, which leaves the service able
    /// to report and remove an entry but not to add one.
    /// </param>
    /// <param name="registryKey">
    /// Which key under HKCU to write to. Only overridden by tests, which have no business adding a
    /// real startup entry to the machine they run on.
    /// </param>
    public WindowsAutoStart(string? executablePath, string registryKey = RunKey)
    {
        _command = executablePath is { Length: > 0 } path ? $"\"{path}\" {StartupArgument}" : string.Empty;
        _key = registryKey;
    }

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_key);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool SetEnabled(bool enabled)
    {
        // Nothing to register. Removing still works, so a stale entry left by an earlier install can
        // always be cleared even when this process can't name its own binary.
        if (enabled && _command.Length == 0)
            return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_key, writable: true);
            if (key is null)
                return false;

            if (enabled)
            {
                // Written every time rather than only when absent, so an entry pointing at an old
                // install location is repaired by turning the setting on again.
                key.SetValue(ValueName, _command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
