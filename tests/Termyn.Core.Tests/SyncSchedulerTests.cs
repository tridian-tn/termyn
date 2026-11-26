using Termyn.Core.Sync;

namespace Termyn.Core.Tests;

public class SyncSchedulerTests
{
    private static readonly SyncCadence Fast = new(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(30));
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_burst_of_writes_produces_a_single_sync()
    {
        var syncs = 0;
        var settled = new TaskCompletionSource();

        // Half a second to a burst spread over a tenth of one. The debounce rolls forward on each
        // write, so what has to hold is that no single gap outlasts it — and a gap asked for as ten
        // milliseconds is not ten milliseconds. Windows wakes a timer on a fifteen-millisecond tick
        // and a loaded machine misses ticks, so at the eighty this was written with, one stretched
        // gap ended the burst early and the sync it triggered was a second one. The margin is the
        // point of the number, not the number.
        await using var scheduler = new SyncScheduler(_ =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
                settled.TrySetResult();
            return Task.CompletedTask;
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(500)));

        scheduler.Start();
        for (var i = 0; i < 10; i++)
        {
            scheduler.NotifyWrite();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await settled.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref syncs));
    }

    [Fact]
    public async Task An_immediate_request_does_not_wait_for_the_debounce()
    {
        var ran = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(5)));

        scheduler.Start();
        scheduler.RequestNow();

        await ran.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_write_before_the_loop_starts_is_not_lost()
    {
        var ran = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        }, Fast);

        scheduler.NotifyWrite();
        scheduler.Start();

        await ran.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_timer_keeps_syncing_without_any_writes()
    {
        var twice = new TaskCompletionSource();
        var count = 0;
        await using var scheduler = new SyncScheduler(_ =>
        {
            if (Interlocked.Increment(ref count) >= 2)
                twice.TrySetResult();
            return Task.CompletedTask;
        }, Fast);

        scheduler.Start();

        await twice.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Work_left_over_from_one_sync_is_flushed_without_waiting_for_the_timer()
    {
        var calls = 0;
        var twice = new TaskCompletionSource();
        // No timer at all: only the "more work pending" signal can produce the second sync.
        await using var scheduler = new SyncScheduler(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n >= 2)
                twice.TrySetResult();
            return Task.FromResult(new SyncOutcome(MoreQueued: n < 2)); // more remains after the first round
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start();
        scheduler.RequestNow();

        await twice.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_sync_that_asks_to_be_retried_is_in_manual_mode_too()
    {
        // No timer at all, and nothing written or asked for: only the retry brings it round. A sync
        // that couldn't read the token otherwise waited for an F5 nobody knew to press.
        var calls = 0;
        var twice = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            if (Interlocked.Increment(ref calls) >= 2) twice.TrySetResult();

            return Task.FromResult(new SyncOutcome(RetryIn: TimeSpan.FromMilliseconds(50)));
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start();
        scheduler.RequestNow();

        await twice.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_write_brings_a_loop_waiting_to_retry_round_sooner()
    {
        // Unlike a pause, which holds off whatever's asked, since there the server is refusing.
        var calls = 0;
        var twice = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            if (Interlocked.Increment(ref calls) >= 2) twice.TrySetResult();

            return Task.FromResult(new SyncOutcome(RetryIn: TimeSpan.FromMinutes(10)));
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start();
        scheduler.RequestNow();
        await WaitFor(() => Volatile.Read(ref calls) == 1);

        scheduler.NotifyWrite();

        await twice.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_retry_isnt_carried_past_a_sync_that_then_fails()
    {
        // The retry time left over from the first sync was long past by the second, so the loop
        // came straight back round after the failure, and again, and again.
        var calls = 0;
        await using var scheduler = new SyncScheduler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) return Task.FromResult(new SyncOutcome(RetryIn: TimeSpan.FromMilliseconds(30)));

            throw new InvalidOperationException("boom");
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start();
        scheduler.RequestNow();
        await WaitFor(() => Volatile.Read(ref calls) >= 2);

        // Nothing else brings a manual loop round, so any more is the stale retry.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_retry_asked_for_before_the_loop_started_is_kept()
    {
        // The sync a window makes as it loads runs before the loop does. Its retry was dropped, so
        // a token that couldn't be read at startup waited for the ordinary interval, or for ever in
        // manual mode, as here.
        var ran = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start(new SyncOutcome(RetryIn: TimeSpan.FromMilliseconds(50)));

        await ran.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_pause_asked_for_before_the_loop_started_is_waited_out()
    {
        // Rate-limited at startup, the loop went on to try again on its ordinary interval.
        var calls = 0;
        await using var scheduler = new SyncScheduler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, Fast);

        scheduler.Start(new SyncOutcome(PauseFor: TimeSpan.FromSeconds(30)));
        scheduler.RequestNow();

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task More_writes_queued_before_the_loop_started_are_gone_straight_back_for()
    {
        var ran = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start(new SyncOutcome(MoreQueued: true));

        await ran.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    /// <summary>Waits until something's true, failing the test if it isn't within the patience allowed.</summary>
    /// <param name="condition">What to wait for</param>
    private static async Task WaitFor(Func<bool> condition)
    {
        var giveUp = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < giveUp, "Gave up waiting.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_failing_sync_is_surfaced_and_the_loop_keeps_running()
    {
        var failures = new TaskCompletionSource<Exception>();
        var again = new TaskCompletionSource();
        var calls = 0;
        await using var scheduler = new SyncScheduler(_ =>
        {
            if (Interlocked.Increment(ref calls) >= 2)
                again.TrySetResult();
            throw new InvalidOperationException("boom");
        }, Fast);
        scheduler.SyncFailed += ex => failures.TrySetResult(ex);

        scheduler.Start();

        var error = await failures.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal("boom", error.Message);

        // Still alive after the failure: it comes round and syncs again. Waited for rather than
        // looked for after a fixed sleep, which a runner that stalls its timers for longer than
        // the sleep less one interval would fail, as a loaded macOS one did.
        await again.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref calls) > 1);
    }

    [Fact]
    public async Task A_cancellation_from_inside_the_sync_does_not_stop_the_loop()
    {
        var calls = 0;
        var reported = new TaskCompletionSource();
        var again = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            if (Interlocked.Increment(ref calls) >= 2)
                again.TrySetResult();
            // Not the scheduler's own shutdown token — this must not be mistaken for one.
            throw new OperationCanceledException(new CancellationToken(canceled: true));
        }, Fast);
        scheduler.SyncFailed += _ => reported.TrySetResult();

        scheduler.Start();

        await reported.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Waited for rather than looked for after a fixed sleep, as in the test above.
        await again.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref calls) > 1);
    }

    [Fact]
    public async Task Disposing_twice_is_safe()
    {
        var scheduler = new SyncScheduler(_ => Task.CompletedTask, Fast);
        scheduler.Start();

        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_does_not_hang_on_a_sync_that_ignores_cancellation()
    {
        var entered = new TaskCompletionSource();
        var scheduler = new SyncScheduler(async _ =>
        {
            entered.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
        }, Fast);

        scheduler.Start();
        await entered.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var dispose = scheduler.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_pause_is_waited_out_even_when_something_wakes_the_loop()
    {
        var calls = 0;
        var paused = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                paused.TrySetResult();
                return Task.FromResult(new SyncOutcome(PauseFor: TimeSpan.FromSeconds(30)));
            }
            return Task.FromResult(SyncOutcome.Done);
        }, Fast);

        scheduler.Start();
        await paused.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The server is refusing requests, so neither a write nor an F5 may cut the wait short.
        for (var i = 0; i < 5; i++)
        {
            scheduler.NotifyWrite();
            scheduler.RequestNow();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task The_loop_comes_back_once_a_pause_has_elapsed()
    {
        var calls = 0;
        var twice = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n >= 2)
                twice.TrySetResult();
            return Task.FromResult(n == 1
                ? new SyncOutcome(PauseFor: TimeSpan.FromMilliseconds(80))
                : SyncOutcome.Done);
            // No timer: only the pause elapsing can produce the second call.
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start();
        scheduler.RequestNow();

        await twice.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_sync_that_no_longer_pauses_clears_the_hold()
    {
        var calls = 0;
        var thrice = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n >= 3)
                thrice.TrySetResult();
            return Task.FromResult(n == 1
                ? new SyncOutcome(PauseFor: TimeSpan.FromMilliseconds(60))
                : SyncOutcome.Done);
        }, Fast);

        scheduler.Start();

        // A third call can only happen if the ordinary interval resumed after the pause.
        await thrice.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_pause_of_nothing_leaves_the_ordinary_interval_in_force()
    {
        var calls = 0;
        var twice = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n >= 2)
                twice.TrySetResult();
            // Zero is what a Retry-After already in the past comes to; it must not hold anything up.
            return Task.FromResult(new SyncOutcome(PauseFor: TimeSpan.Zero));
        }, Fast);

        scheduler.Start();

        await twice.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Starting_twice_runs_one_loop()
    {
        var calls = 0;
        var ran = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            Interlocked.Increment(ref calls);
            ran.TrySetResult();
            return Task.CompletedTask;
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start();
        scheduler.Start();
        scheduler.RequestNow();

        await ran.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref calls));
    }
}
