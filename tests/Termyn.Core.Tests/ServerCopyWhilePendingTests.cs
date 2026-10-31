using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// What becomes of the server's copy of something a queued write of ours still owns.
/// </summary>
/// <remarks>
/// It used to be dropped, and the sync token moved past it all the same, so it was gone for good. A
/// write that then failed put back the state from before it, which was stale by then, and nothing
/// from the server would ever say otherwise.
/// </remarks>
public class ServerCopyWhilePendingTests
{
    private readonly FixedClock _clock = new(new DateOnly(2026, 7, 31));
    private readonly FakeApi _api = new();
    private readonly InMemorySnapshotStore _store = new();

    public ServerCopyWhilePendingTests()
    {
        _store.PutResource("items", "a", """{"id":"a","content":"A","description":"","project_id":"p","child_order":1}""");
        _store.PutResource("items", "b", """{"id":"b","content":"B","description":"","project_id":"p","child_order":2}""");
    }

    [Fact]
    public async Task An_edit_still_waiting_keeps_its_field_and_takes_the_rest_from_the_server()
    {
        var engine = Engine();
        engine.UpdateItem("a", new JsonObject { ["description"] = "mine" });

        await Unanswered(engine, """{"id":"a","content":"Renamed on the web","description":"theirs","project_id":"p","child_order":1}""");

        var a = engine.Snapshot().Items.Single(i => i.Id == "a");
        Assert.Equal("mine", a.Description);
        Assert.Equal("Renamed on the web", a.Content);
        Assert.Equal(1, engine.PendingCount);
    }

    [Fact]
    public async Task An_edit_that_fails_after_the_server_changed_the_task_puts_back_what_it_has_now()
    {
        var engine = Engine();
        engine.UpdateItem("a", new JsonObject { ["description"] = "mine" });

        await Unanswered(engine, """{"id":"a","content":"Renamed on the web","description":"theirs","project_id":"p","child_order":1}""");
        await GivenUpOn(engine);

        var a = engine.Snapshot().Items.Single(i => i.Id == "a");
        Assert.Equal("theirs", a.Description);
        Assert.Equal("Renamed on the web", a.Content);
        Assert.Equal(1, engine.FailedCount);

        // And so does the store.
        Assert.Equal("theirs", Reloaded().Snapshot().Items.Single(i => i.Id == "a").Description);
    }

    [Fact]
    public async Task Two_edits_of_one_field_that_both_fail_put_back_what_the_server_has_now()
    {
        // The first holds the server's value and the second what the first put there, so rolling
        // back both, in order, ends on the server's.
        var engine = Engine();
        engine.UpdateItem("a", new JsonObject { ["content"] = "First" });
        engine.UpdateItem("a", new JsonObject { ["content"] = "Second" });

        await Unanswered(engine, """{"id":"a","content":"Renamed on the web","description":"","project_id":"p","child_order":1}""");
        Assert.Equal("Second", engine.Snapshot().Items.Single(i => i.Id == "a").Content);

        await GivenUpOn(engine);

        Assert.Equal("Renamed on the web", engine.Snapshot().Items.Single(i => i.Id == "a").Content);
        Assert.Equal(2, engine.FailedCount);
    }

    [Fact]
    public async Task A_delete_that_fails_after_the_server_changed_the_task_puts_back_what_it_has_now()
    {
        var engine = Engine();
        engine.DeleteItem("a");

        await Unanswered(engine, """{"id":"a","content":"Renamed on the web","description":"","project_id":"p","child_order":1}""");
        Assert.DoesNotContain(engine.Snapshot().Items, i => i.Id == "a");

        await GivenUpOn(engine);

        Assert.Equal("Renamed on the web", engine.Snapshot().Items.Single(i => i.Id == "a").Content);
    }

    [Fact]
    public async Task A_close_still_waiting_keeps_the_tick_and_takes_the_rest()
    {
        var engine = Engine();
        engine.CompleteItem("a");

        await Unanswered(engine, """{"id":"a","content":"Renamed on the web","checked":false,"description":"","project_id":"p","child_order":1}""");

        var a = engine.Snapshot().Items.Single(i => i.Id == "a");
        Assert.True(a.Completed);
        Assert.Equal("Renamed on the web", a.Content);

        // Undone, it's the server's task with the tick taken off.
        Assert.True(engine.Undo());
        a = engine.Snapshot().Items.Single(i => i.Id == "a");
        Assert.False(a.Completed);
        Assert.Equal("Renamed on the web", a.Content);
    }

    [Fact]
    public async Task A_reorder_that_fails_after_the_server_moved_the_task_puts_back_where_it_has_it()
    {
        var engine = Engine();
        Assert.True(engine.MoveItem("b", -1));
        Assert.Equal(1, engine.Snapshot().Items.Single(i => i.Id == "b").ChildOrder);

        await Unanswered(engine, """{"id":"b","content":"B","description":"","project_id":"p","child_order":7}""");
        Assert.Equal(1, engine.Snapshot().Items.Single(i => i.Id == "b").ChildOrder);

        await GivenUpOn(engine);

        Assert.Equal(7, engine.Snapshot().Items.Single(i => i.Id == "b").ChildOrder);
    }

    private SyncEngine Engine()
    {
        var engine = new SyncEngine(_api, _store, new FakeSecrets { Stored = "tok" }, _clock, attemptCeiling: 2);
        engine.Load();
        return engine;
    }

    private SyncEngine Reloaded()
    {
        var engine = new SyncEngine(new FakeApi(), _store, new FakeSecrets { Stored = "tok" }, _clock);
        engine.Load();
        return engine;
    }

    /// <summary>A round that says nothing about what was sent, and brings the server's copy of a task.</summary>
    private async Task Unanswered(SyncEngine engine, string task)
    {
        _api.Next = _ => new SyncResponse
        {
            SyncToken = "s2",
            Changes = [Json.Change("items", Json.Object(task)["id"]!.ToString(), task)],
        };
        await engine.SyncAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A second round of silence, a while later, which is the ceiling: the writes fail.</summary>
    private async Task GivenUpOn(SyncEngine engine)
    {
        _clock.Advance(TimeSpan.FromMinutes(5));
        _api.Next = _ => new SyncResponse { SyncToken = "s3" };
        await engine.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, engine.PendingCount);
    }
}
