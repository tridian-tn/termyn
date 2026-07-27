namespace Termyn.Core.Sync;

/// <summary>How often Termyn reconciles with Todoist in the background.</summary>
public sealed record SyncCadence(TimeSpan Interval, TimeSpan WriteDebounce)
{
    /// <summary>A background sync every 45 seconds, with writes coalesced over 800 ms.</summary>
    public static readonly SyncCadence Default = new(TimeSpan.FromSeconds(45), TimeSpan.FromMilliseconds(800));

    /// <summary>No timer: sync only on an explicit request or after a write.</summary>
    public static readonly SyncCadence Manual = new(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(800));
}

/// <summary>
/// Runs the background sync loop. A write nudges it rather than syncing immediately, so a burst of
/// edits produces one round trip instead of many, keeping well inside Todoist's request limits.
/// </summary>
public sealed class SyncScheduler : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _sync;
    private readonly SyncCadence _cadence;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;
    private DateTimeOffset _writePendingSince;
    private bool _writePending;

    public SyncScheduler(Func<CancellationToken, Task> sync, SyncCadence? cadence = null)
    {
        _sync = sync;
        _cadence = cadence ?? SyncCadence.Default;
    }

    /// <summary>Raised when a background sync throws, so the shell can surface it.</summary>
    public event Action<Exception>? SyncFailed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stopping.Token));

    /// <summary>Notes that something was written; the loop flushes once the debounce elapses.</summary>
    public void NotifyWrite()
    {
        _writePendingSince = DateTimeOffset.UtcNow;
        _writePending = true;
        Wake();
    }

    /// <summary>Asks for a sync as soon as possible, bypassing the debounce.</summary>
    public void RequestNow()
    {
        _writePending = false;
        Wake();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        Wake();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stopping.Dispose();
        _wake.Dispose();
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = _writePending ? _cadence.WriteDebounce : _cadence.Interval;
            try
            {
                await _wake.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested)
                return;

            // Hold off while edits are still arriving, so a burst coalesces into one sync.
            if (_writePending && DateTimeOffset.UtcNow - _writePendingSince < _cadence.WriteDebounce)
                continue;

            _writePending = false;
            try
            {
                await _sync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SyncFailed?.Invoke(ex);
            }
        }
    }
}
