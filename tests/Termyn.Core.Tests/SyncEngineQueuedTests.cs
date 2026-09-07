using System.Text.Json.Nodes;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// That the engine says so when it has queued a write.
/// </summary>
/// <remarks>
/// The loop that flushes the outbox otherwise comes round on its own clock, every forty-five
/// seconds, and a write made in between sits there until it does. The window used to say so at
/// each place it wrote — which worked until somebody added a place and didn't, and a comment sat
/// under "Not sent yet" on a window that was online and idle.
///
/// Every write goes into the outbox through one method, so this is said once from there. What
/// these hold is that it really is every write, since the value of the whole arrangement is that
/// nobody has to remember.
/// </remarks>
public class SyncEngineQueuedTests
{
    [Fact]
    public void Adding_a_task_says_so()
    {
        var (engine, told) = Listening();

        engine.AddItem(new JsonObject { ["content"] = "Buy milk" });

        Assert.Equal(1, told());
    }

    [Fact]
    public void Adding_a_comment_says_so_like_any_other_write()
    {
        // The one that started this. It queued its write and told nobody.
        var (engine, told) = Listening();
        var id = engine.AddItem(new JsonObject { ["content"] = "Buy milk" });

        engine.AddComment(id, "a thought");

        Assert.Equal(2, told());
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("complete")]
    [InlineData("move")]
    public void Every_kind_of_write_says_so(string kind)
    {
        // Named rather than counted, so a write added later that doesn't reach the outbox through
        // the usual door fails here rather than going quiet in the app.
        var (engine, told) = Listening();
        var id = engine.AddItem(new JsonObject { ["content"] = "Buy milk" });
        var before = told();

        switch (kind)
        {
            case "edit":
                engine.UpdateItem(id, new JsonObject { ["content"] = "Buy oats" });
                break;
            case "delete":
                engine.DeleteItem(id);
                break;
            case "complete":
                engine.CompleteItem(id);
                break;
            case "move":
                engine.AddItem(new JsonObject { ["content"] = "Another" });
                engine.IndentItem(id);
                break;
        }

        Assert.True(told() > before, $"{kind} queued a write and said nothing");
    }

    [Fact]
    public void A_listener_that_throws_does_not_lose_the_write()
    {
        // By the time this is said the write is in the store and in the outbox. A subscriber that
        // threw would unwind through the caller and have the window report a failure for a write
        // that had already happened — and skip the refresh that would have shown it.
        var engine = Engine(new InMemorySnapshotStore());
        engine.Queued += () => throw new InvalidOperationException("the loop is having a bad day");

        var id = engine.AddItem(new JsonObject { ["content"] = "Buy milk" });

        Assert.NotNull(id);
        Assert.Equal(1, engine.PendingCount);
        Assert.Equal("Buy milk", engine.Snapshot().Items.Single().Content);
    }

    [Fact]
    public void Reading_says_nothing()
    {
        // It is a write that the loop has to be woken for. Waking it for a look at the model would
        // be a sync on every keystroke that filters the list.
        var (engine, told) = Listening();
        engine.AddItem(new JsonObject { ["content"] = "Buy milk" });
        var after = told();

        _ = engine.Snapshot();
        _ = engine.Outbox;
        _ = engine.PendingCount;

        Assert.Equal(after, told());
    }

    [Fact]
    public void Loading_the_outbox_back_off_the_disk_says_nothing()
    {
        // These are the same writes being remembered rather than new ones being made, and the loop
        // syncs on the way up regardless. Saying so here would be a sync for every unsent write the
        // app was closed with.
        var store = new InMemorySnapshotStore();

        var first = Engine(store);
        first.AddItem(new JsonObject { ["content"] = "Buy milk" });
        Assert.Equal(1, first.PendingCount);

        var told = 0;
        var second = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        second.Queued += () => told++;
        second.Load();

        Assert.Equal(1, second.PendingCount);   // it did come back
        Assert.Equal(0, told);                  // and it wasn't announced as new
    }

    private static readonly DateOnly Today = new(2026, 7, 31);

    private static SyncEngine Engine(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return engine;
    }

    /// <summary>An engine with something counting what it says.</summary>
    private static (SyncEngine Engine, Func<int> Told) Listening()
    {
        var engine = Engine(new InMemorySnapshotStore());

        var told = 0;
        engine.Queued += () => told++;

        return (engine, () => told);
    }
}
