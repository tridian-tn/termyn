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
        await using var scheduler = new SyncScheduler(_ =>
        {
            if (Interlocked.Increment(ref syncs) == 1)
                settled.TrySetResult();
            return Task.CompletedTask;
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(80)));

        scheduler.Start();
        for (var i = 0; i < 10; i++)
        {
            scheduler.NotifyWrite();
            await Task.Delay(10);
        }

        await settled.Task.WaitAsync(Patience);
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

        await ran.Task.WaitAsync(Patience);
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

        await ran.Task.WaitAsync(Patience);
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

        await twice.Task.WaitAsync(Patience);
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
            return Task.FromResult(n < 2); // more remains after the first round
        }, new SyncCadence(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(20)));

        scheduler.Start();
        scheduler.RequestNow();

        await twice.Task.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_failing_sync_is_surfaced_and_the_loop_keeps_running()
    {
        var failures = new TaskCompletionSource<Exception>();
        var calls = 0;
        await using var scheduler = new SyncScheduler(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        }, Fast);
        scheduler.SyncFailed += ex => failures.TrySetResult(ex);

        scheduler.Start();

        var error = await failures.Task.WaitAsync(Patience);
        Assert.Equal("boom", error.Message);

        // Still alive after the failure.
        await Task.Delay(200);
        Assert.True(Volatile.Read(ref calls) > 1);
    }

    [Fact]
    public async Task A_cancellation_from_inside_the_sync_does_not_stop_the_loop()
    {
        var calls = 0;
        var reported = new TaskCompletionSource();
        await using var scheduler = new SyncScheduler(_ =>
        {
            Interlocked.Increment(ref calls);
            // Not the scheduler's own shutdown token — this must not be mistaken for one.
            throw new OperationCanceledException(new CancellationToken(canceled: true));
        }, Fast);
        scheduler.SyncFailed += _ => reported.TrySetResult();

        scheduler.Start();

        await reported.Task.WaitAsync(Patience);
        await Task.Delay(200);
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
        await entered.Task.WaitAsync(Patience);

        var dispose = scheduler.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
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

        await ran.Task.WaitAsync(Patience);
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref calls));
    }
}
