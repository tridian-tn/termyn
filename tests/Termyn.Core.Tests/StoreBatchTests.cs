using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;
using static Termyn.TestSupport.Flushing;

namespace Termyn.Core.Tests;

/// <summary>
/// Several writes made as one: one commit to the store, and when the store gives out partway,
/// nothing of them left in memory or on disk.
/// </summary>
public class StoreBatchTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- One commit ----------------------------------------------------------------------------

    [Fact]
    public void Tasks_deleted_together_are_saved_in_one_commit()
    {
        // Each commit waits on the disk, and one for each task made a batch of five hundred take
        // 0.4 seconds.
        var inner = Account();
        var store = new FillingStore(inner);
        var engine = NewEngine(store);

        engine.DeleteItems(["a", "b", "c"]);

        Assert.Equal(1, store.Commits);
        Assert.Equal(3, inner.Load().Outbox.Count(c => c.Type == "item_delete"));
    }

    [Fact]
    public void Tasks_ticked_off_together_are_saved_in_one_commit()
    {
        var store = new FillingStore(Account());
        var engine = NewEngine(store);

        engine.CompleteItems(["a", "b"]);

        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public void Tasks_put_back_together_are_saved_in_one_commit()
    {
        var store = new FillingStore(Account());
        var engine = NewEngine(store);
        engine.CompleteItems(["a", "b"]);

        engine.ReopenItems(["a", "b"]);

        Assert.Equal(2, store.Commits);
    }

    [Fact]
    public void An_undo_of_tasks_deleted_together_is_saved_in_one_commit()
    {
        var store = new FillingStore(Account());
        var engine = NewEngine(store);
        engine.DeleteItems(["a", "b", "c"]);

        Assert.True(engine.Undo());

        Assert.Equal(2, store.Commits);
    }

    // ---- A store that gives out ----------------------------------------------------------------

    [Fact]
    public void A_batch_the_store_gives_out_partway_through_leaves_nothing_changed()
    {
        // A single write the store refuses changes nothing, since it's refused before anything
        // does. Left ahead of the store, the window showed a batch as done that a restart lost.
        var inner = Account();
        var store = new FillingStore(inner) { WritesLeft = 2 };
        var engine = NewEngine(store);

        Assert.Throws<IOException>(() => engine.DeleteItems(["a", "b", "c"]));

        Assert.Equal(["a", "b", "c", "d"], Held(engine));
        Assert.Empty(engine.Outbox);
        Assert.False(engine.CanUndo);

        var saved = inner.Load();
        Assert.Empty(saved.Outbox);
        Assert.Equal(4, saved.Resources.Count(r => r.Type == "items"));
    }

    [Fact]
    public void An_undo_the_store_gives_out_partway_through_leaves_the_batch_to_undo_again()
    {
        var inner = Account();
        var store = new FillingStore(inner);
        var engine = NewEngine(store);
        engine.DeleteItems(["a", "b", "c"]);
        store.WritesLeft = 1;

        Assert.Throws<IOException>(() => engine.Undo());

        Assert.Equal(["d"], Held(engine));
        Assert.Equal(3, engine.Outbox.Count);
        Assert.Equal(3, inner.Load().Outbox.Count);

        // Still one press for the lot, once there's room.
        store.WritesLeft = int.MaxValue;
        Assert.True(engine.Undo());
        Assert.Equal(["a", "b", "c", "d"], Held(engine));
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public async Task An_undo_of_tasks_already_sent_the_store_gives_out_on_brings_them_all_back_next_time()
    {
        // Once the deletes have gone there's nothing queued to drop, so the tasks come back only
        // from what the undo remembered of them. That has to survive the undo that didn't happen.
        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var store = new FillingStore(Account());
        var engine = NewEngine(store, api);
        engine.DeleteItems(["a", "b", "c"]);
        await Flush(engine, api);
        store.WritesLeft = 1;

        Assert.Throws<IOException>(() => engine.Undo());
        Assert.Equal(["d"], Held(engine));

        store.WritesLeft = int.MaxValue;
        Assert.True(engine.Undo());
        Assert.Equal(["a", "b", "c", "d"], engine.Snapshot().Items.Select(i => i.Content).Order());
    }

    [Fact]
    public async Task A_batch_the_store_gives_out_on_keeps_the_completed_tasks_fetched_from_the_archive()
    {
        // Deleting one of them takes it out of what was fetched. That has to come back with the
        // rest when the batch doesn't happen.
        var api = new FakeApi
        {
            Completed = _ => new CompletedPage(
                [Json.Change("items", "done", """{"id":"done","content":"Done","project_id":"p1","checked":true}""")],
                null),
        };
        var store = new FillingStore(Account()) { WritesLeft = 1 };
        var engine = NewEngine(store, api);
        await engine.FetchCompletedAsync(TestContext.Current.CancellationToken);

        Assert.Throws<IOException>(() => engine.DeleteItems(["done", "a"]));

        Assert.Contains(engine.Snapshot().CompletedItems, i => i.Id == "done");
    }

    [Fact]
    public void A_batch_the_store_gives_out_on_puts_back_what_it_changed_in_writes_still_queued()
    {
        // Letting an edit go hands what it found to the next edit of the same field, which changes
        // a command that's already queued. Kept after the batch was taken back, the second edit
        // thought the first had never been made, and letting it go put back the wrong text.
        var store = new FillingStore(Account());
        var engine = NewEngine(store);
        engine.UpdateItem("a", new JsonObject { ["content"] = "B" });
        engine.UpdateItem("a", new JsonObject { ["content"] = "C" });
        var first = engine.Outbox[0].Uuid;
        var second = engine.Outbox[1].Uuid;
        store.WritesLeft = 1;

        Assert.Throws<IOException>(() => engine.Together(() =>
        {
            engine.Revert(first);
            engine.DeleteItem("d");
        }));

        store.WritesLeft = int.MaxValue;
        engine.Revert(second);
        Assert.Equal("B", engine.Snapshot().Items.Single(i => i.Id == "a").Content);
    }

    // ---- A batch's own handle ------------------------------------------------------------------

    /// <summary>The stores a batch is tried against.</summary>
    public enum Kind
    {
        Memory,
        Disk,
    }

    [Theory]
    [InlineData(Kind.Memory)]
    [InlineData(Kind.Disk)]
    public void A_batch_disposed_a_second_time_leaves_the_one_after_it_alone(Kind kind)
    {
        // Acting on whichever batch the store had open, the second dispose threw away the next
        // batch's writes, and its commit then had nothing to keep.
        using var opened = new Opened(kind);
        var store = opened.Store;
        var first = store.BeginBatch();
        first.Dispose();

        using (var second = store.BeginBatch())
        {
            store.PutResource("items", "i1", """{"id":"i1"}""");
            first.Dispose();
            second.Commit();
        }

        Assert.Equal("i1", Assert.Single(store.Load().Resources).Id);
    }

    [Theory]
    [InlineData(Kind.Memory)]
    [InlineData(Kind.Disk)]
    public void A_batch_committed_after_it_was_thrown_away_is_refused_and_keeps_nothing_of_the_next(Kind kind)
    {
        using var opened = new Opened(kind);
        var store = opened.Store;
        var first = store.BeginBatch();
        first.Dispose();

        using (store.BeginBatch())
        {
            store.PutResource("items", "i1", """{"id":"i1"}""");
            Assert.Throws<InvalidOperationException>(() => first.Commit());
        }

        Assert.Empty(store.Load().Resources);
    }

    [Theory]
    [InlineData(Kind.Memory)]
    [InlineData(Kind.Disk)]
    public void A_batch_thrown_away_leaves_the_store_as_it_found_it(Kind kind)
    {
        // Every kind of write, and one resource written twice, so what goes back is what was
        // there before the first of them rather than after it.
        using var opened = new Opened(kind);
        var store = opened.Store;
        store.PutResource("items", "kept", """{"id":"kept","v":1}""");
        store.PutResource("items", "gone", """{"id":"gone"}""");
        var queued = Command("queued");
        store.ApplyLocalWrite(queued, [], []);
        store.ApplyLocalWrite(Command("dropped"), [], []);
        var before = Describe(store.Load());

        using (store.BeginBatch())
        {
            store.PutResource("items", "kept", """{"id":"kept","v":2}""");
            store.PutResource("items", "kept", """{"id":"kept","v":3}""");
            store.DeleteResource("items", "gone");
            store.RenameResource("items", "kept", "renamed");
            store.ApplyLocalWrite(Command("added"), [new StoredResource("items", "new", """{"id":"new"}""")], []);
            queued.Attempts = 3;
            store.UpdateCommand(queued);
            store.DeleteCommands(["dropped"]);
            store.SaveSync([], [], "token-2");
        }

        Assert.Equal(before, Describe(store.Load()));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static List<string> Held(SyncEngine engine) => engine.Snapshot().Items.Select(i => i.Id).Order().ToList();

    private static OutboxCommand Command(string uuid) => new() { Uuid = uuid, Type = "item_update", ArgsJson = "{}" };

    /// <summary>Everything a store holds, written out so two of them can be compared.</summary>
    private static string Describe(StoredSnapshot snapshot) => string.Join(
        "\n",
        snapshot.Resources.OrderBy(r => r.Id).Select(r => $"{r.Type}/{r.Id} {r.Json}")
            .Concat(snapshot.Outbox.OrderBy(c => c.Seq).Select(c => $"{c.Uuid} tried {c.Attempts}"))
            .Append($"token {snapshot.SyncToken}"));

    /// <summary>An empty store of the kind asked for, and its file cleared away after it.</summary>
    private sealed class Opened : IDisposable
    {
        private readonly string? _path;

        public Opened(Kind kind)
        {
            if (kind == Kind.Memory)
            {
                Store = new InMemorySnapshotStore();
                return;
            }

            _path = Path.Combine(Path.GetTempPath(), "termyn-test-" + Guid.NewGuid().ToString("N") + ".db");
            Store = new SqliteSnapshotStore(_path);
        }

        public ISnapshotStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            if (_path is null) return;

            foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>A project and four tasks in it.</summary>
    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");

        foreach (var (id, order) in new[] { ("a", 1), ("b", 2), ("c", 3), ("d", 4) })
        {
            var item = new JsonObject { ["id"] = id, ["content"] = id, ["project_id"] = "p1", ["child_order"] = order };
            store.PutResource("items", id, item.ToJsonString());
        }

        return store;
    }

    private static SyncEngine NewEngine(ISnapshotStore store, FakeApi? api = null)
    {
        var engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return engine;
    }
}
