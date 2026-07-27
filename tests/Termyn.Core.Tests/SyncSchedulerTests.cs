using Termyn.Core.Sync;

namespace Termyn.Core.Tests;

public class SyncSchedulerTests
{
    private static readonly SyncCadence Fast = new(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(30));

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

        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        await twice.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        var error = await failures.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("boom", error.Message);

        // Still alive after the failure.
        await Task.Delay(150);
        Assert.True(Volatile.Read(ref calls) > 1);
    }
}
