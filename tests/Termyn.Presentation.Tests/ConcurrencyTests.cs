using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The background sync loop mutates the model on a worker thread while the UI thread reads it to
/// render. Reading off-gate used to throw "Collection was modified" within a second of the two
/// overlapping; both of these fail against that version.
/// </summary>
public class ConcurrencyTests
{
    private static readonly TimeSpan RunFor = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task A_write_publishing_while_a_background_sync_churns_the_model_does_not_throw()
    {
        var presenter = Churning(out var cts);

        var failure = await RaceAsync(presenter, cts, () =>
        {
            // Each write republishes, enumerating the model the worker is adding to and removing from.
            presenter.Rename("stable", "Renamed " + Environment.TickCount);
            presenter.SetPriority("stable", Priority.P2);
        });

        Assert.Null(failure);
    }

    [Fact]
    public async Task Reading_rows_while_a_background_sync_churns_the_model_does_not_throw()
    {
        var presenter = Churning(out var cts);

        var failure = await RaceAsync(presenter, cts, () =>
        {
            presenter.Search(presenter.Rows.Count % 2 == 0 ? "Task" : string.Empty);
            _ = presenter.Rows.Select(r => r.Content).ToList();
            _ = presenter.Status;
        });

        Assert.Null(failure);
    }

    [Fact]
    public async Task Ticking_off_and_folding_while_a_background_sync_publishes_does_not_throw()
    {
        // Each publish reads what's been ticked off and what's folded while it builds the outline.
        // Both used to be changed on the window's thread with nothing keeping the publish out.
        // Fifty of them, so a publish spends long enough reading the set for a change to land
        // in the middle of it. With one, the read is over before anything can collide.
        var presenter = Churning(out var cts, steady: 50);
        var steady = Enumerable.Range(0, 50).Select(i => $"steady{i}").ToList();

        var failure = await RaceAsync(presenter, cts, () =>
        {
            foreach (var id in steady)
            {
                if (presenter.Rows.FirstOrDefault(r => r.Id == id) is { Completed: true })
                    presenter.Reopen(id);
                else
                    presenter.Complete(id);

                presenter.SetCollapsed(id, !presenter.IsCollapsed(id));
            }

            presenter.DropTicked();
            presenter.FoldAll(presenter.Rows.Count % 2 == 0);
        });

        Assert.Null(failure);
    }

    [Fact]
    public async Task The_first_sync_is_waited_for_like_the_loops_own()
    {
        // Shutdown waits for syncs before closing the cache. It used to count only the loop's, so
        // the one the window starts on loading could still be writing when the cache was closed.
        using var answer = new ManualResetEventSlim();
        var api = new FakeApi
        {
            Next = _ =>
            {
                answer.Wait(TestContext.Current.CancellationToken);
                return new SyncResponse { SyncToken = "s1" };
            },
        };

        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" });
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(new DateOnly(2026, 7, 31))));

        Assert.True(presenter.WaitForSyncs(TimeSpan.Zero));

        var loading = Task.Run(() => presenter.LoadAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await WaitUntil(() => !presenter.WaitForSyncs(TimeSpan.Zero));

        Assert.False(presenter.WaitForSyncs(TimeSpan.FromMilliseconds(100)));

        answer.Set();
        await loading;

        Assert.True(presenter.WaitForSyncs(TimeSpan.Zero));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Gave up waiting.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>An engine whose every sync adds a fresh batch of tasks and tombstones the last one.</summary>
    /// <param name="steady">How many tasks besides "stable" are there throughout, as steady0, steady1…</param>
    private static MainPresenter Churning(out CancellationTokenSource cts, int steady = 0)
    {
        var round = 0;
        var api = new FakeApi
        {
            Next = _ =>
            {
                var n = Interlocked.Increment(ref round);
                var changes = new List<ResourceChange>();
                for (var i = 0; i < 400; i++)
                    changes.Add(Json.Change("items", $"i{n}-{i}", $$"""{"id":"i{{n}}-{{i}}","content":"Task {{i}}","child_order":{{i}}}"""));
                for (var i = 0; i < 400; i++)
                    changes.Add(Json.Deleted("items", $"i{n - 1}-{i}"));
                return new SyncResponse { SyncToken = $"s{n}", Changes = changes };
            },
        };

        var store = new InMemorySnapshotStore();
        store.PutResource("items", "stable", """{"id":"stable","content":"Stable","child_order":0}""");
        for (var i = 0; i < steady; i++)
            store.PutResource("items", $"steady{i}", $$"""{"id":"steady{{i}}","content":"Steady {{i}}","child_order":{{i + 1}}}""");

        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        cts = new CancellationTokenSource(RunFor);
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(new DateOnly(2026, 7, 31))));
    }

    private static async Task<Exception?> RaceAsync(MainPresenter presenter, CancellationTokenSource cts, Action uiWork)
    {
        Exception? failure = null;

        var syncing = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                    await presenter.SyncAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        });

        var interacting = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                    uiWork();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        });

        await Task.WhenAll(syncing, interacting);
        cts.Dispose();
        return failure;
    }
}
