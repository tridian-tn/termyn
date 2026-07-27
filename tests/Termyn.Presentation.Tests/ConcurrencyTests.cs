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

    /// <summary>An engine whose every sync adds a fresh batch of tasks and tombstones the last one.</summary>
    private static MainPresenter Churning(out CancellationTokenSource cts)
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
