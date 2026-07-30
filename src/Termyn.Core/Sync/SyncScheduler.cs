namespace Termyn.Core.Sync;

/// <summary>How often Termyn reconciles with Todoist in the background.</summary>
/// <param name="Interval">
/// Time between background syncs. <see cref="Timeout.InfiniteTimeSpan"/> turns the timer off, which
/// is manual mode: the loop then only runs when a write or an explicit request wakes it.
/// </param>
public sealed record SyncCadence(TimeSpan Interval, TimeSpan WriteDebounce)
{
    /// <summary>A background sync every 45 seconds, with writes coalesced over 800 ms.</summary>
    public static readonly SyncCadence Default = new(TimeSpan.FromSeconds(45), TimeSpan.FromMilliseconds(800));
}

/// <summary>What one sync round tells the loop to do next.</summary>
/// <param name="MoreQueued">More writes are waiting than one round could flush.</param>
/// <param name="PauseFor">
/// Hold off this long before trying again — the server asked us to. Nothing wakes the loop early
/// out of a pause, since the reason for it is that requests are being refused.
/// </param>
public sealed record SyncOutcome(bool MoreQueued = false, TimeSpan? PauseFor = null)
{
    public static readonly SyncOutcome Done = new();
}

/// <summary>
/// Runs the background sync loop. A write nudges it rather than syncing immediately, so a burst of
/// edits produces one round trip instead of many, keeping well inside Todoist's request limits.
/// </summary>
/// <remarks>
/// <see cref="Start"/> and <see cref="DisposeAsync"/> are expected from one thread;
/// <see cref="NotifyWrite"/> and <see cref="RequestNow"/> may be called from any.
/// <see cref="SyncFailed"/> is raised on the worker thread, so a UI subscriber must marshal.
/// </remarks>
public sealed class SyncScheduler : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<SyncOutcome>> _sync;
    private readonly SyncCadence _cadence;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _state = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>Monotonic: a wall-clock step backwards would otherwise stall the debounce forever.</summary>
    private long _writePendingAt;

    private bool _writePending;

    /// <summary>When the loop may next try, as a tick count. Zero when it isn't holding off.</summary>
    private long _pausedUntil;

    /// <param name="sync">Performs one sync and says what the loop should do next.</param>
    public SyncScheduler(Func<CancellationToken, Task<SyncOutcome>> sync, SyncCadence? cadence = null)
    {
        _sync = sync;
        _cadence = cadence ?? SyncCadence.Default;
    }

    public SyncScheduler(Func<CancellationToken, Task> sync, SyncCadence? cadence = null)
        : this(async ct => { await sync(ct); return SyncOutcome.Done; }, cadence)
    {
    }

    /// <summary>Raised on the worker thread when a background sync throws.</summary>
    public event Action<Exception>? SyncFailed;

    public void Start()
    {
        lock (_state)
        {
            if (_disposed)
                return;
            _loop ??= Task.Run(() => RunAsync(_stopping.Token));
        }
    }

    /// <summary>Notes that something was written; the loop flushes once the debounce elapses.</summary>
    public void NotifyWrite()
    {
        lock (_state)
        {
            _writePendingAt = Environment.TickCount64;
            _writePending = true;
        }
        Wake();
    }

    /// <summary>Asks for a sync as soon as possible, bypassing the debounce.</summary>
    public void RequestNow()
    {
        lock (_state)
            _writePending = false;
        Wake();
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_state)
        {
            if (_disposed)
                return;
            _disposed = true;
            loop = _loop;
        }

        // Never resume on the caller's context: shutdown blocks on this from the UI thread, and by
        // then the message loop that would have to run the continuation has stopped.
        await _stopping.CancelAsync().ConfigureAwait(false);
        Wake();

        if (loop is not null)
        {
            try
            {
                // Bounded: a sync that ignores its token must not hold up shutting down.
                await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Expected on shutdown.
            }
        }

        _stopping.Dispose();
        _wake.Dispose();
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another caller released first; the loop is already awake.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            lock (_state)
            {
                var remaining = _pausedUntil - Environment.TickCount64;
                wait = remaining > 0
                    ? TimeSpan.FromMilliseconds(remaining)
                    : _writePending ? _cadence.WriteDebounce : _cadence.Interval;
            }

            try
            {
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested)
                return;

            lock (_state)
            {
                // Something woke the loop mid-pause — a write, or an F5. The server is refusing
                // requests, so the wake is noted and the wait resumes rather than being cut short.
                if (Environment.TickCount64 < _pausedUntil)
                    continue;

                // Hold off while edits are still arriving, so a burst coalesces into one sync.
                if (_writePending && Environment.TickCount64 - _writePendingAt < _cadence.WriteDebounce.TotalMilliseconds)
                    continue;
                _writePending = false;
            }

            try
            {
                var outcome = await _sync(ct).ConfigureAwait(false);

                lock (_state)
                {
                    _pausedUntil = outcome.PauseFor is { Ticks: > 0 } pause
                        ? Environment.TickCount64 + (long)pause.TotalMilliseconds
                        : 0;
                }

                if (outcome.MoreQueued)
                    Wake(); // more queued than one round could flush; come straight back
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Including a cancellation that came from inside the sync rather than from shutdown:
                // that must not silently stop the loop for the rest of the session.
                try
                {
                    SyncFailed?.Invoke(ex);
                }
                catch
                {
                    // A subscriber's failure must not take the loop down with it.
                }
            }
        }
    }
}
