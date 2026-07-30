using System.Diagnostics;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Perf.Tests;

/// <summary>
/// The parts of the spec's performance budget that can be checked without a desktop: the work that
/// happens before the first paint, and the cost of a write reaching the screen.
/// </summary>
/// <remarks>
/// Deliberately loose. These are regression gates, not benchmarks — they exist to catch the day
/// someone makes the outline projection quadratic, not to measure the machine. The budgets are set
/// several times the measured cost so that a busy build agent doesn't fail the build; the real
/// figures are recorded in docs/performance.md, measured on the reference profile.
/// </remarks>
public class PerformanceBudgetTests : IDisposable
{
    /// <summary>Rounds to take, keeping the best. One slow round is the machine, not the code.</summary>
    private const int Rounds = 8;

    /// <summary>
    /// Rounds run before any are counted. .NET compiles a method cheaply first and only replaces it
    /// with optimised code after it has been called enough times, so an unwarmed measurement is
    /// partly of the interpreter — it read three times slower than the same code in the app.
    /// </summary>
    private const int WarmUp = 60;

    private readonly string _dir = Directory.CreateTempSubdirectory("termyn-perf").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The SQLite handle can outlive the test by a moment; a temp directory is no great loss.
        }
    }

    [Fact]
    public void The_cache_loads_and_projects_well_inside_the_startup_budget()
    {
        var path = Seeded();
        var best = TimeSpan.MaxValue;

        for (var round = 0; round < Rounds; round++)
        {
            // A fresh store each round, so this is a cold read of the file rather than a warm one of
            // the connection's own caches — which is what a cold start actually does.
            using var store = new SqliteSnapshotStore(path);
            var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));

            var watch = Stopwatch.StartNew();
            engine.Load();
            var presenter = new MainPresenter(engine, Parser(), new FixedClock(ReferenceAccount.Today));
            watch.Stop();

            Assert.Equal(ReferenceAccount.Tasks, engine.Snapshot().Items.Count);
            Assert.NotEmpty(presenter.Rows);

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        // The spec allows 500 ms from launch to interactive, of which process start, the runtime and
        // WinForms take the bulk. This is the part Termyn's own code owns.
        Assert.True(best < TimeSpan.FromMilliseconds(400), $"load and first projection took {best.TotalMilliseconds:N0} ms");
    }

    [Fact]
    public void Nothing_about_starting_up_touches_the_network()
    {
        // The budget rests on this: a start that waited on Todoist would be at the mercy of the
        // network however fast the local work was.
        var api = new ThrowingApi();
        using var store = new SqliteSnapshotStore(Seeded());
        var engine = new SyncEngine(api, store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));

        engine.Load();
        var presenter = new MainPresenter(engine, Parser(), new FixedClock(ReferenceAccount.Today));
        presenter.Select(ViewSelection.Of(SmartView.Today));
        presenter.Search("task");

        Assert.NotEmpty(presenter.Rows);
    }

    [Fact]
    public void A_write_reaches_the_rows_within_a_frame()
    {
        using var store = new SqliteSnapshotStore(Seeded());
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
        engine.Load();
        var presenter = new MainPresenter(engine, Parser(), new FixedClock(ReferenceAccount.Today));
        presenter.Select(ViewSelection.Of(SmartView.All));

        for (var i = 0; i < WarmUp; i++)
            presenter.SetPriority($"i{i}", Priority.P2);

        var best = TimeSpan.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            var id = $"i{round}";
            var watch = Stopwatch.StartNew();
            presenter.SetPriority(id, Priority.P1);
            watch.Stop();

            Assert.Equal(Priority.P1, presenter.Rows.Single(r => r.Id == id).Priority);

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        // The spec allows one frame at 60 fps for a write to become visible, and a write includes
        // the durable outbox append and a re-projection of the view. Doubled, so a build agent under
        // load doesn't fail the build for being busy.
        Assert.True(best < TimeSpan.FromMilliseconds(32), $"a write took {best.TotalMilliseconds:N1} ms to reach the rows");
    }

    [Fact]
    public void Switching_view_reprojects_within_a_frame()
    {
        using var store = new SqliteSnapshotStore(Seeded());
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
        engine.Load();
        var presenter = new MainPresenter(engine, Parser(), new FixedClock(ReferenceAccount.Today));

        ViewSelection[] views =
        [
            ViewSelection.Of(SmartView.Today),
            ViewSelection.Of(SmartView.Upcoming),
            ViewSelection.Of(SmartView.Inbox),
            ViewSelection.OfProject("p3"),
            ViewSelection.OfLabel("label4"),
        ];

        // Warmed properly, so this measures the projection rather than code the runtime has not
        // got round to optimising yet.
        for (var i = 0; i < WarmUp; i++)
            presenter.Select(views[i % views.Length]);

        var best = TimeSpan.MaxValue;
        foreach (var view in views)
        {
            var watch = Stopwatch.StartNew();
            presenter.Select(view);
            watch.Stop();

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        Assert.True(best < TimeSpan.FromMilliseconds(32), $"switching view took {best.TotalMilliseconds:N1} ms");
    }

    [Fact]
    public void Searching_the_whole_account_stays_interactive_per_keystroke()
    {
        using var store = new SqliteSnapshotStore(Seeded());
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
        engine.Load();
        var presenter = new MainPresenter(engine, Parser(), new FixedClock(ReferenceAccount.Today));

        // Search runs over every loaded task, not just the current view, and it runs on every
        // keystroke — so it is the one projection that has to keep up with typing.
        for (var i = 0; i < WarmUp; i++)
            presenter.Search(i % 2 == 0 ? "some" : "someth");

        var best = TimeSpan.MaxValue;
        foreach (var query in new[] { "s", "so", "som", "some", "someth", "something" })
        {
            var watch = Stopwatch.StartNew();
            presenter.Search(query);
            watch.Stop();

            Assert.NotEmpty(presenter.Rows);

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        Assert.True(best < TimeSpan.FromMilliseconds(32), $"a search keystroke took {best.TotalMilliseconds:N1} ms");
    }

    [Fact]
    public void The_sidebar_counts_come_off_one_pass_rather_than_one_per_node()
    {
        // A scan per project and per section would be quadratic in the account, and would show up
        // here as a projection that grows far faster than the task count.
        var small = Measure(500);
        var large = Measure(5_000);

        // Ten times the tasks, generously under thirty times the work. A per-node scan would be
        // hundreds of times slower, not tens.
        Assert.True(
            large < TimeSpan.FromTicks(Math.Max(small.Ticks, TimeSpan.TicksPerMillisecond) * 30),
            $"500 tasks projected in {small.TotalMilliseconds:N1} ms but 5,000 took {large.TotalMilliseconds:N1} ms");

        TimeSpan Measure(int tasks)
        {
            var store = new InMemorySnapshotStore();
            SeedItems(store, tasks);
            var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
            engine.Load();
            var presenter = new MainPresenter(engine, Parser(), new FixedClock(ReferenceAccount.Today));

            for (var i = 0; i < WarmUp; i++)
                presenter.Select(ViewSelection.Of(SmartView.Today));

            var best = TimeSpan.MaxValue;
            for (var round = 0; round < Rounds; round++)
            {
                var watch = Stopwatch.StartNew();
                presenter.Select(ViewSelection.Of(SmartView.Today));
                watch.Stop();
                if (watch.Elapsed < best)
                    best = watch.Elapsed;
            }
            return best;
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    /// <summary>A cache file holding the reference account, built once per test.</summary>
    private string Seeded()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        using var store = new SqliteSnapshotStore(path);
        ReferenceAccount.Seed(store);
        return path;
    }

    private static void SeedItems(ISnapshotStore store, int tasks)
    {
        var resources = new List<StoredResource>(tasks + ReferenceAccount.Projects);
        for (var p = 0; p < ReferenceAccount.Projects; p++)
            resources.Add(new StoredResource(ResourceType.Projects, $"p{p}", $$"""{"id":"p{{p}}","name":"Project {{p}}"}"""));

        for (var i = 0; i < tasks; i++)
        {
            var due = ReferenceAccount.Today.AddDays((i % 28) - 14);
            resources.Add(new StoredResource(ResourceType.Items, $"i{i}",
                $$$"""{"id":"i{{{i}}}","content":"Task {{{i}}}","project_id":"p{{{i % ReferenceAccount.Projects}}}","child_order":{{{i}}},"labels":["label{{{i % 5}}}"],"due":{"date":"{{{due:yyyy-MM-dd}}}"}}"""));
        }

        store.SaveSync(resources, [], "seeded-token");
    }

    private static QuickAddParser Parser() => new(new FixedClock(ReferenceAccount.Today));

    /// <summary>An API that fails the test if anything reaches for the network.</summary>
    private sealed class ThrowingApi : ITodoistApi
    {
        public Task<SyncResponse> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not sync.");

        public Task<ResourceChange> QuickAddAsync(string token, string text, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not quick-add.");

        public Task<CompletedPage> GetCompletedAsync(string token, CompletedQuery query, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not fetch completed tasks.");

        public Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not validate the token.");
    }
}
