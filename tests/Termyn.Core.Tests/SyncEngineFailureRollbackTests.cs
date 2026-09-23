using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// What happens to the local copy when a write is never going to land, and what survives a restart.
/// Nothing else heals these: the server has no reason to resend a resource that never changed
/// there, and the sync token has moved past it.
/// </summary>
public class SyncEngineFailureRollbackTests
{
    // ---- Rolling back a write that failed for good ----------------------------------------------

    [Fact]
    public async Task A_delete_that_fails_for_good_puts_the_project_and_its_contents_back()
    {
        var store = Seeded();
        var (engine, api) = Engine(store);

        engine.DeleteProject("p1");
        Fails(api, engine);

        await engine.SyncAsync();
        await engine.SyncAsync(); // the ceiling is two attempts

        var snapshot = engine.Snapshot();
        Assert.Equal("Work", snapshot.Projects.Single(p => p.Id == "p1").Name);
        Assert.Equal("Admin", snapshot.Sections.Single().Name);
        Assert.Equal(["Task"], snapshot.Items.Select(i => i.Content));
        Assert.Equal(1, engine.FailedCount); // the user is still told it went wrong
    }

    [Fact]
    public async Task A_label_delete_that_fails_for_good_puts_it_back_on_the_tasks()
    {
        // The quietest of these: a label simply missing from a dozen tasks is not something anyone
        // would notice, and nothing would ever put it back.
        var store = Seeded();
        var (engine, api) = Engine(store);

        engine.DeleteLabel("l1");
        Fails(api, engine);

        await engine.SyncAsync();
        await engine.SyncAsync();

        Assert.Equal("home", engine.Snapshot().Labels.Single().Name);
        Assert.Equal(["home"], engine.Snapshot().Items.Single().Labels);
    }

    [Fact]
    public async Task An_edit_that_fails_for_good_goes_back_to_the_servers_version()
    {
        var store = Seeded();
        var (engine, api) = Engine(store);

        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        Fails(api, engine);

        await engine.SyncAsync();
        await engine.SyncAsync();

        Assert.Equal("Task", engine.Snapshot().Items.Single().Content);
    }

    [Fact]
    public async Task A_command_the_server_never_rules_on_is_rolled_back_too()
    {
        // No verdict at the ceiling is the same outcome as a rejection: it isn't going to land.
        var store = Seeded();
        var (engine, api) = Engine(store);

        engine.DeleteProject("p1");
        api.Next = _ => new SyncResponse { SyncToken = "s2" };

        await engine.SyncAsync();
        await engine.SyncAsync();

        Assert.Single(engine.Snapshot().Projects);
        Assert.Equal(1, engine.FailedCount);
    }

    [Fact]
    public async Task A_create_the_server_never_rules_on_keeps_what_was_typed()
    {
        // It may well have been applied, and there is no prior to go back to — dropping it would
        // throw away the user's own text on a guess.
        var store = new InMemorySnapshotStore();
        var (engine, api) = Engine(store);

        engine.AddItem(new JsonObject { ["content"] = "Written offline" });
        api.Next = _ => new SyncResponse { SyncToken = "s2" };

        await engine.SyncAsync();
        await engine.SyncAsync();

        Assert.Equal(["Written offline"], engine.Snapshot().Items.Select(i => i.Content));
        Assert.Equal(1, engine.FailedCount);
    }

    [Fact]
    public async Task A_rolled_back_delete_stops_holding_the_resource_against_the_server()
    {
        // Once the write is abandoned the resource is the server's again, so its changes apply.
        var store = Seeded();
        var (engine, api) = Engine(store);

        engine.DeleteProject("p1");
        Fails(api, engine);
        await engine.SyncAsync();
        await engine.SyncAsync();

        api.Response = new SyncResponse
        {
            SyncToken = "s3",
            Changes = [new ResourceChange("projects", "p1", false, Json.Object("""{"id":"p1","name":"Renamed elsewhere"}"""))],
        };
        await engine.SyncAsync();

        Assert.Equal("Renamed elsewhere", engine.Snapshot().Projects.Single(p => p.Id == "p1").Name);
    }

    // ---- Rolling back a write to something the server has named since ---------------------------

    [Fact]
    public async Task A_refused_edit_leaves_one_copy_of_a_task_the_server_has_named_since()
    {
        // Added offline and edited before it went. The server takes the add and names the task, and
        // refuses the edit. Put back under the name the edit recorded, there'd be two of it, for good.
        var store = Seeded();
        var (engine, api) = Engine(store, attemptCeiling: 1);

        var temp = engine.AddItem(new JsonObject { ["content"] = "Written offline", ["project_id"] = "p1" });
        engine.UpdateItem(temp, new JsonObject { ["content"] = "Edited offline" });

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(
                c => c.Uuid,
                c => c.Type == "item_update" ? new CommandResult(false, "ERR", "rejected") : new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = "real" },
        };
        await engine.SyncAsync();

        var added = Assert.Single(engine.Snapshot().Items, i => i.Id != "i1");
        Assert.Equal("real", added.Id);
        Assert.Equal("Written offline", added.Content);
        Assert.DoesNotContain(store.Load().Resources, r => r.Id == temp);
    }

    [Fact]
    public async Task A_refused_delete_puts_a_task_back_under_the_names_the_server_has_given_since()
    {
        // Deleted while its add was on the wire, so the delete goes in the next round under the
        // server's name for it. What it recorded still has the names from here, for the task and
        // for the parent it was added under — put back as recorded, it'd be a task the server has
        // never heard of, under a parent that isn't held.
        var store = Seeded();
        var (engine, api) = Engine(store, attemptCeiling: 1);

        var parent = engine.AddItem(new JsonObject { ["content"] = "Parent", ["project_id"] = "p1" });
        var child = engine.AddItem(new JsonObject { ["content"] = "Child", ["project_id"] = "p1", ["parent_id"] = parent });

        api.Next = commands =>
        {
            engine.DeleteItem(child);
            return new SyncResponse
            {
                SyncToken = "s2",
                SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
                TempIdMapping = new Dictionary<string, string> { [parent] = "parent", [child] = "child" },
            };
        };
        await engine.SyncAsync();

        Fails(api, engine);
        await engine.SyncAsync();

        var restored = Assert.Single(engine.Snapshot().Items, i => i.Content == "Child");
        Assert.Equal("child", restored.Id);
        Assert.Equal("parent", restored.ParentId);
        Assert.DoesNotContain(store.Load().Resources, r => r.Id == child);
    }

    // ---- Undo across a restart -------------------------------------------------------------------

    [Fact]
    public void An_unflushed_project_delete_is_still_undoable_after_a_restart()
    {
        var store = Seeded();
        var (engine, _) = Engine(store);
        engine.DeleteProject("p1");

        var restarted = Reload(store);

        Assert.True(restarted.Undo());
        Assert.Equal("Work", restarted.Snapshot().Projects.Single(p => p.Id == "p1").Name);
        Assert.Equal("Task", restarted.Snapshot().Items.Single().Content);
    }

    [Fact]
    public void An_unflushed_label_delete_is_still_undoable_after_a_restart()
    {
        var store = Seeded();
        var (engine, _) = Engine(store);
        engine.DeleteLabel("l1");

        var restarted = Reload(store);

        Assert.True(restarted.Undo());
        Assert.Single(restarted.Snapshot().Labels);
    }

    [Fact]
    public void Undo_after_a_restart_reverses_the_delete_before_the_completion_under_it()
    {
        var store = Seeded();
        var (engine, _) = Engine(store);

        engine.CompleteItem("i1");
        engine.DeleteSection("s1");

        var restarted = Reload(store);

        // The delete was the last thing done, so it is the first thing undone. Leaving it off the
        // stack meant Ctrl+Z reached past it and un-completed the task instead — undoing something
        // the user wasn't thinking about while the delete they meant stayed put.
        Assert.True(restarted.Undo());
        Assert.Single(restarted.Snapshot().Sections);
        Assert.True(restarted.Snapshot().Items.Single(i => i.Id == "i1").Completed);
    }

    [Fact]
    public async Task A_queued_reorder_does_not_become_an_undo_barrier_after_a_restart()
    {
        // A reorder keeps its priors in an array as well, but of bare tasks rather than entries
        // naming a type. Reading that as a cascading delete puts a barrier in front of a write that
        // is perfectly reversible — and acking the reorder leaves the barrier behind for good,
        // because an ack deliberately doesn't forget undo records.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"First","project_id":"p1","child_order":1}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Second","project_id":"p1","child_order":2}""");
        var (engine, _) = Engine(store);

        engine.CompleteItem("i1");
        engine.ReorderItems(["i2", "i1"]);

        var (restarted, api) = Engine(store);

        // The reorder lands, so it is no longer in the outbox to be reverted from.
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };
        await restarted.SyncAsync();

        // Ctrl+Z should still reach the completion underneath it.
        Assert.True(restarted.Undo());
        Assert.False(restarted.Snapshot().Items.Single(i => i.Id == "i1").Completed);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static InMemorySnapshotStore Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p1"}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"home"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Task","project_id":"p1","labels":["home"]}""");
        return store;
    }

    private static (SyncEngine Engine, FakeApi Api) Engine(InMemorySnapshotStore store, int attemptCeiling = 2)
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(new DateOnly(2026, 7, 31)), attemptCeiling: attemptCeiling);
        engine.Load();
        return (engine, api);
    }

    private static SyncEngine Reload(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(new DateOnly(2026, 7, 31)), attemptCeiling: 2);
        engine.Load();
        return engine;
    }

    /// <summary>Has the server reject everything the engine sends.</summary>
    private static void Fails(FakeApi api, SyncEngine engine)
        => api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected")),
        };
}
