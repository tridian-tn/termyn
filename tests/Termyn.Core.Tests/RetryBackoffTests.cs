using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// How long a write the server refused for now, or didn't answer, is left before it goes again.
/// </summary>
/// <remarks>
/// It used to go again on the very next round, and the loop came straight back for it, so all its
/// attempts were spent in the few seconds a busy server takes to recover and the change was rolled
/// back for nothing.
/// </remarks>
public class RetryBackoffTests
{
    private readonly FixedClock _clock = new(new DateOnly(2026, 7, 31));
    private readonly FakeApi _api = new();

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(429)]
    public async Task A_write_refused_for_now_is_left_a_while_before_it_goes_again(int status)
    {
        var engine = Engine();
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });

        RefuseAll(status);
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, engine.PendingCount);
        Assert.False(engine.WritesDue);

        // Another round straight away, a write nudging the loop say, doesn't send it or spend a try.
        await engine.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_api.LastCommands);
        Assert.Equal(1, engine.Outbox.Single().Attempts);

        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(engine.WritesDue);
        await engine.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Single(_api.LastCommands);
    }

    [Fact]
    public async Task Each_refusal_leaves_it_longer()
    {
        var engine = Engine();
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        RefuseAll(503);

        await engine.SyncAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(engine.WritesDue);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(engine.WritesDue);
    }

    [Fact]
    public async Task One_refused_for_now_still_fails_at_the_ceiling_with_its_change_put_back()
    {
        var engine = Engine(attemptCeiling: 3);
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        RefuseAll(503);

        for (var i = 0; i < 3; i++)
        {
            await engine.SyncAsync(TestContext.Current.CancellationToken);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(1, engine.FailedCount);
        Assert.Equal("Task", engine.Snapshot().Items.Single().Content);
    }

    [Fact]
    public async Task A_refusal_that_wont_change_goes_the_way_it_always_did()
    {
        // A 4xx other than a rate limit: tried again next round, and failed at the ceiling.
        var engine = Engine(attemptCeiling: 2);
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        RefuseAll(400);

        await engine.SyncAsync(TestContext.Current.CancellationToken);
        Assert.True(engine.WritesDue);

        await engine.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, engine.FailedCount);
    }

    [Fact]
    public async Task A_write_left_unanswered_is_left_a_while_too()
    {
        var engine = Engine();
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        _api.Next = _ => new SyncResponse { SyncToken = "s2" };

        await engine.SyncAsync(TestContext.Current.CancellationToken);
        Assert.False(engine.WritesDue);

        await engine.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_api.LastCommands);
        Assert.Equal(1, engine.Outbox.Single().NoVerdictRounds);
    }

    [Fact]
    public async Task A_create_refused_for_now_is_tried_again_rather_than_given_up_on()
    {
        // A refused create is failed on the spot, as a definite no. A 503 isn't one.
        var engine = Engine();
        var temp = engine.AddItem(new JsonObject { ["content"] = "Written offline" });
        RefuseAll(503);

        await engine.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, engine.PendingCount);
        Assert.Equal(0, engine.FailedCount);
        Assert.Contains(engine.Snapshot().Items, i => i.Id == temp);
    }

    [Fact]
    public async Task What_was_queued_after_waits_behind_it()
    {
        // Writes go in the order they were made, so one built on the write being left mustn't go
        // ahead of it.
        var engine = Engine();
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        RefuseAll(503);
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        engine.UpdateItem("i1", new JsonObject { ["description"] = "later" });
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_api.LastCommands);
        Assert.Equal(2, engine.PendingCount);

        _clock.Advance(TimeSpan.FromSeconds(15));
        await engine.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["item_update", "item_update"], _api.LastCommands.Select(c => c.Type));
    }

    [Fact]
    public async Task A_write_to_something_else_goes_without_waiting_for_it()
    {
        // Only what builds on the write being left has to wait. Holding back everything behind it
        // kept every later edit to unrelated tasks unsent for as long as the one write was refused.
        var store = Store();
        store.PutResource("items", "i2", """{"id":"i2","content":"Other","project_id":"p1"}""");
        var engine = Engine(store);

        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        RefuseAll(503);
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        engine.UpdateItem("i1", new JsonObject { ["description"] = "later" });
        engine.UpdateItem("i2", new JsonObject { ["content"] = "Unrelated" });
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        var sent = Assert.Single(_api.LastCommands);
        Assert.Equal("i2", sent.Args["id"]?.ToString());
        Assert.Equal(2, engine.Outbox.Count(c => c.State == OutboxState.Pending && c.ArgsJson.Contains("\"i1\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_restart_lets_it_go_again_straight_away()
    {
        var store = Store();
        var engine = Engine(store);
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        RefuseAll(503);
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        var restarted = Engine(store);

        Assert.True(restarted.WritesDue);
    }

    private SyncEngine Engine(InMemorySnapshotStore? store = null, int attemptCeiling = 5)
    {
        var engine = new SyncEngine(_api, store ?? Store(), new FakeSecrets { Stored = "tok" }, _clock, attemptCeiling: attemptCeiling);
        engine.Load();
        return engine;
    }

    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Task","project_id":"p1"}""");
        return store;
    }

    private void RefuseAll(int status)
        => _api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(false, "42", "refused", status)),
        };
}
