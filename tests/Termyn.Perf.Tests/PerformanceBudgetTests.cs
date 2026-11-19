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

    /// <summary>
    /// A loose ceiling for an interactive operation reaching the rows — not a literal frame. The real
    /// within-a-frame figures are measured on the reference profile and live in docs/performance.md;
    /// this is a regression gate, there to catch the day a projection turns quadratic (seconds over
    /// 5,000 tasks), not to time the machine. It's set well above both the measured single-digit-ms
    /// cost and the jitter of a busy build agent — a tighter 32 ms here failed intermittently on main
    /// with nothing changed, because best-of-several still landed at 32–64 ms under load.
    /// </summary>
    private static readonly TimeSpan InteractiveCeiling = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The part of the spec's 500 ms from launch to interactive that Termyn's own code owns: loading
    /// the cache and the first projection. Measured at 111–141 ms in Release on the development
    /// laptop.
    /// </summary>
    /// <remarks>
    /// Not raised to suit a slow build agent, unlike <see cref="InteractiveCeiling"/>. A limit loose
    /// enough to ride out a busy agent would let start-up get slower a little at a time, which is
    /// what this is here to stop. More rounds steady it instead (<see cref="StartupRounds"/>), and
    /// <see cref="StartupAllocationCeiling"/> catches the creep that no time limit on a shared agent
    /// can.
    /// </remarks>
    private static readonly TimeSpan StartupBudget = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Rounds the start-up measurement takes, keeping the best. More than the others take, since
    /// its budget isn't loosened: a busy patch on an agent passes, and a real slowdown shows in
    /// every round.
    /// </summary>
    private const int StartupRounds = 20;

    /// <summary>
    /// What loading the cache and the first projection may allocate, in bytes. Unlike a time, the
    /// count comes out the same on a busy agent as on an idle laptop, so it can sit close to the
    /// measured cost and catch the slow creep that a time limit can't tell from a slow machine.
    /// </summary>
    /// <remarks>
    /// Measured at 30.3 MB on 18 Nov 2026, the same in Debug and Release, so this is about 10%
    /// above it. When a change has good reason to cost more, raise it in the same change and say
    /// why. When one makes start-up cheaper, lower it, so the room doesn't quietly fill up.
    /// </remarks>
    private const long StartupAllocationCeiling = 33_000_000;

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
        var leastAllocated = long.MaxValue;

        for (var round = 0; round < StartupRounds; round++)
        {
            // A fresh store each round, so this is a cold read of the file rather than a warm one of
            // the connection's own caches — which is what a cold start actually does.
            using var store = new SqliteSnapshotStore(path);
            var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));

            // Loading and projecting stay on this thread, so its own count is all of their
            // allocations and none of the tests running alongside.
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            engine.Load();
            var presenter = new MainPresenter(engine, Parser(), new FixedClock(ReferenceAccount.Today));
            watch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.Equal(ReferenceAccount.Tasks, engine.Snapshot().Items.Count);
            Assert.NotEmpty(presenter.Rows);

            if (watch.Elapsed < best)
                best = watch.Elapsed;

            // The least of the rounds, as the first pays for setting up statics that another test
            // may already have done, depending on the order they ran in.
            leastAllocated = Math.Min(leastAllocated, allocated);
        }

        Assert.True(
            leastAllocated < StartupAllocationCeiling,
            $"load and first projection allocated {leastAllocated / 1_000_000.0:N1} MB");

        Assert.True(best < StartupBudget, $"load and first projection took {best.TotalMilliseconds:N0} ms");
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

        // A write includes the durable outbox append and a re-projection of the view. Held to the
        // loose interactive ceiling, not a literal frame — the frame figure is in docs/performance.md.
        Assert.True(best < InteractiveCeiling, $"a write took {best.TotalMilliseconds:N1} ms to reach the rows");
    }

    [Fact]
    public void Ticking_a_task_off_reaches_the_rows_within_a_frame()
    {
        // The write people make most, and one that looks through the whole account for sub-tasks
        // to take with it. Held to the same frame as any other write.
        using var store = new SqliteSnapshotStore(Seeded());
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
        engine.Load();
        var presenter = new MainPresenter(engine, Parser(), new FixedClock(ReferenceAccount.Today));
        presenter.Select(ViewSelection.Of(SmartView.All));

        // Tasks with a sub-task under them, so each close has something to take with it — and none
        // that repeat, which move on to their next date rather than being ticked off.
        var parents = Enumerable.Range(0, ReferenceAccount.Tasks)
            .Where(i => i % 4 == 0 && i % 10 != 0)
            .Select(i => $"i{i}")
            .Take(WarmUp + Rounds)
            .ToList();

        foreach (var id in parents.Take(WarmUp))
            presenter.Complete(id);

        var best = TimeSpan.MaxValue;
        foreach (var id in parents.Skip(WarmUp))
        {
            var watch = Stopwatch.StartNew();
            presenter.Complete(id);
            watch.Stop();

            Assert.True(presenter.Rows.Single(r => r.Id == id).Completed);

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        Assert.True(best < InteractiveCeiling, $"ticking a task off took {best.TotalMilliseconds:N1} ms to reach the rows");
    }

    [Fact]
    public void Deleting_every_task_in_the_account_together_stays_interactive()
    {
        // One Ctrl+Z for the lot means the undo stack counts batches rather than writes. Counted by
        // a walk of the stack for every write, a batch of thousands was quadratic, and made with
        // the sync's lock held.
        var best = TimeSpan.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var store = new InMemorySnapshotStore();
            ReferenceAccount.Seed(store);
            var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
            engine.Load();
            var ids = engine.Snapshot().Items.Select(i => i.Id).ToList();

            var watch = Stopwatch.StartNew();
            engine.DeleteItems(ids);
            watch.Stop();

            Assert.Empty(engine.Snapshot().Items);

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        Assert.True(best < InteractiveCeiling, $"deleting {ReferenceAccount.Tasks:N0} tasks together took {best.TotalMilliseconds:N0} ms");
    }

    [Fact]
    public void A_batch_of_thousands_going_off_the_bottom_of_the_undo_stack_stays_interactive()
    {
        // Past fifty presses the oldest goes, all of it. Taken a write at a time from the front,
        // each one shifted the rest of the stack along and then walked it again to count it.
        var best = TimeSpan.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var store = new InMemorySnapshotStore();
            ReferenceAccount.Seed(store);
            var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
            engine.Load();
            var ids = engine.Snapshot().Items.Select(i => i.Id).ToList();

            // The batch, and then fifty presses of their own on top of it: the last of those is the
            // one that pushes the batch off.
            engine.DeleteItems(ids.Take(ids.Count - 50).ToList());
            foreach (var id in ids.Skip(ids.Count - 50).SkipLast(1))
                engine.DeleteItem(id);

            var watch = Stopwatch.StartNew();
            engine.DeleteItem(ids[^1]);
            watch.Stop();

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        Assert.True(best < InteractiveCeiling, $"letting go of a batch of {ReferenceAccount.Tasks - 50:N0} took {best.TotalMilliseconds:N1} ms");
    }

    /// <summary>How many tasks the batches on disk below are made of.</summary>
    /// <remarks>
    /// Twice the five hundred they were first timed at, so a commit made for each task is several
    /// times the ceiling rather than a little over it: 0.7 seconds to delete them that way, and 1.4
    /// to undo it, against about 30 ms each in one commit, in Debug on the development laptop.
    /// The undo was 53–74 ms until the queue was indexed by uuid, and over the ceiling with the
    /// whole suite running alongside it.
    /// </remarks>
    private const int DiskBatch = 1_000;

    [Fact]
    public void Deleting_a_thousand_tasks_together_on_disk_stays_interactive()
    {
        // Each commit waits on the disk. Made one for each task, five hundred deleted together took
        // 0.4 seconds, so they're made in one. The same thousand each round, put back untimed in
        // between, so there are rounds enough for a busy agent's slow one to be outvoted.
        using var store = new SqliteSnapshotStore(Seeded());
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
        engine.Load();
        var batch = engine.Snapshot().Items.Select(i => i.Id).Take(DiskBatch).ToList();

        var best = TimeSpan.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            var watch = Stopwatch.StartNew();
            engine.DeleteItems(batch);
            watch.Stop();

            Assert.Equal(ReferenceAccount.Tasks - DiskBatch, engine.Snapshot().Items.Count);
            Assert.True(engine.Undo());

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        Assert.True(best < InteractiveCeiling, $"deleting {DiskBatch:N0} tasks together on disk took {best.TotalMilliseconds:N0} ms");
    }

    [Fact]
    public void Undoing_a_thousand_tasks_deleted_together_on_disk_stays_interactive()
    {
        // The undo is one transaction too. Made one for each task, putting back five hundred took
        // 0.7 seconds.
        using var store = new SqliteSnapshotStore(Seeded());
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
        engine.Load();
        var batch = engine.Snapshot().Items.Select(i => i.Id).Take(DiskBatch).ToList();

        var best = TimeSpan.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            engine.DeleteItems(batch);

            var watch = Stopwatch.StartNew();
            Assert.True(engine.Undo());
            watch.Stop();

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        Assert.Equal(ReferenceAccount.Tasks, engine.Snapshot().Items.Count);
        Assert.True(best < InteractiveCeiling, $"undoing {DiskBatch:N0} tasks deleted together on disk took {best.TotalMilliseconds:N0} ms");
    }

    /// <summary>
    /// How many tasks the whole-account undo below puts back: twice the reference account, where
    /// work that walks the queue once for each task takes seconds.
    /// </summary>
    private const int WholeAccount = 2 * ReferenceAccount.Tasks;

    /// <summary>
    /// A loose ceiling for undoing a delete of <see cref="WholeAccount"/> tasks at once, as a
    /// regression gate rather than a measurement. Measured at 0.27–0.30 s in Debug on the
    /// development laptop, against the 4 s or so it takes when each task undone walks the queue.
    /// It's not an interactive figure: nobody deletes ten thousand tasks to see it happen quickly.
    /// </summary>
    private static readonly TimeSpan WholeAccountCeiling = TimeSpan.FromSeconds(1);

    [Fact]
    public void Undoing_every_task_in_a_large_account_deleted_together_takes_one_pass()
    {
        // Each write undone was looked for from the oldest end of the queue, though a batch's
        // writes are the newest, and was forgotten twice, the second time by walking the whole
        // undo stack for a write already taken off it.
        var best = TimeSpan.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            var store = new InMemorySnapshotStore();
            ReferenceAccount.Seed(store, WholeAccount);
            var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets(), new FixedClock(ReferenceAccount.Today));
            engine.Load();
            engine.DeleteItems(engine.Snapshot().Items.Select(i => i.Id).ToList());

            var watch = Stopwatch.StartNew();
            Assert.True(engine.Undo());
            watch.Stop();

            Assert.Equal(WholeAccount, engine.Snapshot().Items.Count);

            if (watch.Elapsed < best)
                best = watch.Elapsed;
        }

        Assert.True(best < WholeAccountCeiling, $"undoing {WholeAccount:N0} tasks deleted together took {best.TotalMilliseconds:N0} ms");
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

        Assert.True(best < InteractiveCeiling, $"switching view took {best.TotalMilliseconds:N1} ms");
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

        Assert.True(best < InteractiveCeiling, $"a search keystroke took {best.TotalMilliseconds:N1} ms");
    }

    [Fact]
    public void The_sidebar_counts_do_not_grow_with_the_number_of_projects()
    {
        // Scaled by node count rather than by task count, because a per-node scan is what this
        // guards against and a per-node scan grows with the nodes. Scaling the tasks instead let the
        // budget collapse to a fixed few milliseconds, which a scan of forty projects fits inside.
        var few = Measure(projects: 40);
        var many = Measure(projects: 400);

        // Ten times the projects. One pass over the tasks barely notices; a scan per project is ten
        // times the work.
        Assert.True(
            many < TimeSpan.FromTicks(Math.Max(few.Ticks, TimeSpan.TicksPerMillisecond / 2) * 5),
            $"40 projects projected in {few.TotalMilliseconds:N1} ms but 400 took {many.TotalMilliseconds:N1} ms");

        TimeSpan Measure(int projects)
        {
            var store = new InMemorySnapshotStore();
            ReferenceAccount.Seed(store, ReferenceAccount.Tasks, projects);
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

    private static QuickAddParser Parser() => new(new FixedClock(ReferenceAccount.Today));

    /// <summary>An API that fails the test if anything reaches for the network.</summary>
    private sealed class ThrowingApi : ITodoistApi
    {
        public Task<SyncResponse> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not sync.");

        public Task<CompletedPage> GetCompletedAsync(string token, CompletedQuery query, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not fetch completed tasks.");

        public Task<TokenProbe> ValidateTokenAsync(string token, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not validate the token.");

        public Task DownloadAsync(string token, string fileUrl, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not fetch an attachment.");

        public Task<System.Text.Json.Nodes.JsonObject> UploadAsync(string token, Stream content, string fileName, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not upload.");

        public Task DeleteUploadAsync(string token, string fileUrl, CancellationToken ct = default)
            => throw new InvalidOperationException("Startup must not delete an upload.");
    }
}
