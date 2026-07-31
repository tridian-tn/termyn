using System.Text.Json.Serialization;
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
    /// <remarks>
    /// Not written to the file. These three are derived from the settings above, and persisting them
    /// would give the user keys they can edit to no effect — and which contradict the real ones the
    /// moment either changes.
    /// </remarks>
    [JsonIgnore]
    public HotkeyBinding HotkeyBinding => HotkeyBinding.ParseOrDefault(Hotkey);

    /// <summary>
    /// The cadence the sync loop should run at. Manual mode still coalesces writes; it just doesn't
    /// poll, so an interval long enough to be effectively off is used rather than a special case in
    /// the loop.
    /// </summary>
    [JsonIgnore]
    public SyncCadence Cadence => new(
        SyncMode == SyncMode.Manual ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(ClampedInterval),
        SyncCadence.Default.WriteDebounce);

    /// <summary>The interval held within the range the spec allows, whatever the file said.</summary>
    [JsonIgnore]
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

    /// <summary>
    /// Compares by what the state holds, not by which list instance holds it. A record compares a
    /// collection by reference, so two view states read back from the same file would otherwise be
    /// unequal — which is exactly the comparison anything checking for changes would want to make.
    /// The collapsed keys are compared as a set, since that is what they are built from.
    /// </summary>
    public bool Equals(ViewState? other)
        => other is not null
           && SelectedKey == other.SelectedKey
           && SidebarWidth == other.SidebarWidth
           && WindowX == other.WindowX
           && WindowY == other.WindowY
           && WindowWidth == other.WindowWidth
           && WindowHeight == other.WindowHeight
           && Maximized == other.Maximized
           && CollapsedKeys.Count == other.CollapsedKeys.Count
           && !CollapsedKeys.Except(other.CollapsedKeys, StringComparer.Ordinal).Any();

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SelectedKey);
        hash.Add(SidebarWidth);
        hash.Add(WindowX);
        hash.Add(WindowY);
        hash.Add(WindowWidth);
        hash.Add(WindowHeight);
        hash.Add(Maximized);

        // Order-independent, to match the comparison: the keys come from a set walked in sidebar
        // order, so renaming a project reorders them without changing what is collapsed.
        hash.Add(CollapsedKeys.Count);
        var keys = 0;
        foreach (var key in CollapsedKeys)
            keys ^= StringComparer.Ordinal.GetHashCode(key);
        hash.Add(keys);

        return hash.ToHashCode();
    }
}
