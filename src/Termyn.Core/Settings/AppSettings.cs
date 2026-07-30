using Termyn.Core.Sync;

namespace Termyn.Core.Settings;

/// <summary>Whether Termyn syncs on a timer or only when asked.</summary>
public enum SyncMode
{
    Automatic,
    Manual,
}

/// <summary>
/// Everything Termyn remembers between runs, minus the token (which lives in the secret store) and
/// the task cache (which lives in the snapshot store).
/// </summary>
public sealed record AppSettings
{
    /// <summary>Bumped whenever a release changes the shape of the file, so it can be migrated.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Below Todoist's own floor a timer would just burn requests; above it feels stale.</summary>
    public const int MinSyncIntervalSeconds = 15;

    public const int MaxSyncIntervalSeconds = 300;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The global quick-add combination, written the way <see cref="HotkeyBinding"/> reads it.</summary>
    public string Hotkey { get; init; } = HotkeyBinding.Default.ToString();

    /// <summary>Off leaves the key to whatever else on the machine wants it.</summary>
    public bool HotkeyEnabled { get; init; } = true;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public SyncMode SyncMode { get; init; } = SyncMode.Automatic;

    public int SyncIntervalSeconds { get; init; } = 45;

    public bool LaunchAtLogin { get; init; }

    /// <summary>Closing the window leaves Termyn in the tray, which keeps the global hotkey live.</summary>
    public bool CloseToTray { get; init; } = true;

    public ViewState View { get; init; } = new();

    /// <summary>The hotkey as a binding, falling back to the default when the file holds nonsense.</summary>
    public HotkeyBinding HotkeyBinding => HotkeyBinding.ParseOrDefault(Hotkey);

    /// <summary>
    /// The cadence the sync loop should run at. Manual mode still coalesces writes; it just doesn't
    /// poll, so an interval long enough to be effectively off is used rather than a special case in
    /// the loop.
    /// </summary>
    public SyncCadence Cadence => new(
        SyncMode == SyncMode.Manual ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(ClampedInterval),
        SyncCadence.Default.WriteDebounce);

    /// <summary>The interval held within the range the spec allows, whatever the file said.</summary>
    public int ClampedInterval => Math.Clamp(SyncIntervalSeconds, MinSyncIntervalSeconds, MaxSyncIntervalSeconds);
}

/// <summary>Where the user had the window and what they were looking at, so a restart lands there.</summary>
public sealed record ViewState
{
    /// <summary>The sidebar row that was selected, as its <c>SidebarKeys</c> key.</summary>
    public string? SelectedKey { get; init; }

    /// <summary>Sidebar branches the user had closed, so a restart doesn't reopen them all.</summary>
    public IReadOnlyList<string> CollapsedKeys { get; init; } = [];

    public int SidebarWidth { get; init; } = 220;

    /// <summary>Null means "wherever the window manager puts it" — the first run, or a lost monitor.</summary>
    public int? WindowX { get; init; }

    public int? WindowY { get; init; }

    public int WindowWidth { get; init; } = 940;

    public int WindowHeight { get; init; } = 580;

    public bool Maximized { get; init; }
}
