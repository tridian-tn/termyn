namespace Termyn.Presentation;

/// <summary>Where the sync loop stands, as the status bar reports it.</summary>
public enum SyncState
{
    /// <summary>Nothing has reconciled yet this session; the rows on screen came off the cache.</summary>
    Never,

    Syncing,
    Synced,

    /// <summary>Todoist was unreachable. The cached view stays, and writes queue.</summary>
    Offline,

    /// <summary>Rate-limited: waiting out the window the server asked for.</summary>
    Paused,

    /// <summary>The token was rejected. Nothing will sync until it is replaced.</summary>
    ReconnectNeeded,
}

/// <summary>
/// The sync half of the status bar. Held as data rather than a string so the view can style it —
/// offline and reconnect deserve more than the same grey as everything else.
/// </summary>
/// <param name="Since">How long ago the last successful sync was, when there has been one.</param>
/// <param name="RetryIn">How long until a paused loop tries again.</param>
public sealed record SyncStatus(
    SyncState State,
    TimeSpan? Since = null,
    TimeSpan? RetryIn = null,
    int Pending = 0,
    int Failed = 0)
{
    /// <summary>The one-line form, already ordered: state first, then what is outstanding.</summary>
    public string Describe()
    {
        var parts = new List<string>(3) { StateText() };

        if (Pending > 0)
            parts.Add($"{Pending} pending");
        if (Failed > 0)
            parts.Add(Failed == 1 ? "1 failed" : $"{Failed} failed");

        return string.Join(" · ", parts);
    }

    private string StateText() => State switch
    {
        SyncState.Syncing => "Syncing…",
        SyncState.Offline => "Offline (showing cached)",
        SyncState.Paused => RetryIn is { } wait ? $"Paused (retry in {Seconds(wait)}s)" : "Paused",
        SyncState.ReconnectNeeded => "Reconnect needed",
        SyncState.Synced => Since is { } ago ? "Synced " + Ago(ago) : "Synced",
        _ => "Not synced yet",
    };

    /// <summary>Rounded up, so a wait of under a second still reads as a second rather than none.</summary>
    private static int Seconds(TimeSpan wait) => Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));

    /// <summary>
    /// A duration in the coarsest unit that still says something. The status bar is glanced at, not
    /// read, so "3m ago" is more use than a count of seconds that changes every time you look.
    /// </summary>
    public static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return elapsed.TotalSeconds switch
        {
            < 5 => "just now",
            < 60 => $"{(int)elapsed.TotalSeconds}s ago",
            < 3600 => $"{(int)elapsed.TotalMinutes}m ago",
            < 86400 => $"{(int)elapsed.TotalHours}h ago",
            _ => $"{(int)elapsed.TotalDays}d ago",
        };
    }
}
