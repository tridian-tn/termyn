using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;

namespace Termyn.Core.Tests;

public class SyncEngineTests
{
    // ---- Reading -------------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_upserts_and_updates_sync_token()
    {
        var api = new FakeApi
        {
            Next = _ => Resp("s1", changes:
            [
                Ch("projects", "p1", """{"id":"p1","name":"Work"}"""),
                Ch("items", "i1", """{"id":"i1","content":"A","checked":false}"""),
            ]),
        };
        var engine = NewEngine(api);

        await engine.SyncAsync();

        Assert.Equal("s1", engine.Model.SyncToken);
        Assert.Equal("A", engine.Model.Items().Single().Content);
        Assert.Equal("Work", engine.Model.Projects().Single().Name);
    }

    [Fact]
    public async Task Reconcile_removes_a_tombstoned_resource()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);

        api.Next = _ => Resp("s1", changes: [Ch("items", "i1", """{"id":"i1","content":"A"}""")]);
        await engine.SyncAsync();
        Assert.Single(engine.Model.Items());

        api.Next = _ => Resp("s2", changes: [ChDeleted("items", "i1")]);
        await engine.SyncAsync();
        Assert.Empty(engine.Model.Items());
    }

    [Fact]
    public async Task A_response_without_a_sync_token_keeps_the_current_one()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);
        api.Next = _ => Resp("s1");
        await engine.SyncAsync();

        api.Next = _ => Resp(null);
        await engine.SyncAsync();

        Assert.Equal("s1", engine.Model.SyncToken);
    }

    [Fact]
    public void Load_skips_unparseable_rows_instead_of_failing()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "good", """{"id":"good","content":"A"}""");
        store.PutResource("items", "bad", "{ this is not json");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets());

        engine.Load();

        Assert.Equal("A", engine.Model.Items().Single().Content);
    }

    // ---- Optimistic writes ---------------------------------------------------------------------

    [Fact]
    public void Optimistic_add_queues_a_command_and_shows_immediately()
    {
        var engine = NewEngine(new FakeApi());

        var temp = engine.AddItem(new JsonObject { ["content"] = "New" });

        Assert.Equal(1, engine.PendingCount);
        Assert.Equal("New", engine.Model.Items().Single().Content);
        Assert.Equal("item_add", engine.Outbox.Single().Type);
        Assert.Equal(temp, engine.Outbox.Single().TempId);
    }

    [Fact]
    public void Add_does_not_send_a_caller_supplied_id()
    {
        var engine = NewEngine(new FakeApi());

        engine.AddItem(new JsonObject { ["content"] = "New", ["id"] = "spoofed" });

        Assert.DoesNotContain("id", Args(engine.Outbox.Single()).Select(kv => kv.Key));
    }

    [Fact]
    public void Complete_marks_checked_optimistically_and_queues_item_close()
    {
        var engine = SeededEngine(out _);

        engine.CompleteItem("i1");

        Assert.True(engine.Model.Items().Single().Completed);
        var cmd = engine.Outbox.Single();
        Assert.Equal("item_close", cmd.Type);
        Assert.Equal("i1", Args(cmd)["id"]!.ToString());
    }

    [Fact]
    public void Delete_removes_the_item_optimistically_and_queues_item_delete()
    {
        var engine = SeededEngine(out _);

        engine.DeleteItem("i1");

        Assert.Empty(engine.Model.Items());
        var cmd = engine.Outbox.Single();
        Assert.Equal("item_delete", cmd.Type);
        Assert.Equal("i1", Args(cmd)["id"]!.ToString());
    }

    [Fact]
    public void Update_of_an_unknown_item_still_queues_a_command_without_throwing()
    {
        var engine = NewEngine(new FakeApi());

        engine.UpdateItem("ghost", new JsonObject { ["content"] = "B" });

        Assert.Equal("item_update", engine.Outbox.Single().Type);
    }

    [Fact]
    public async Task Sync_without_a_stored_token_throws_before_calling_the_api()
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = null });

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SyncAsync());
        Assert.Empty(api.LastCommands);
    }

    // ---- Revert --------------------------------------------------------------------------------

    [Fact]
    public void Revert_of_a_create_drops_the_command_and_the_optimistic_object()
    {
        var engine = NewEngine(new FakeApi());
        engine.AddItem(new JsonObject { ["content"] = "Draft" });

        engine.Revert(engine.Outbox.Single().Uuid);

        Assert.Equal(0, engine.PendingCount);
        Assert.Empty(engine.Model.Items());
    }

    [Fact]
    public void Revert_of_an_update_restores_the_last_server_state()
    {
        var engine = SeededEngine(out _);
        engine.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" });

        engine.Revert(engine.Outbox.Single().Uuid);

        Assert.Equal(0, engine.PendingCount);
        Assert.Equal("A", engine.Model.Items().Single().Content);
    }

    [Fact]
    public void Revert_of_a_delete_restores_the_item()
    {
        var engine = SeededEngine(out _);
        engine.DeleteItem("i1");

        engine.Revert(engine.Outbox.Single().Uuid);

        Assert.Equal("A", engine.Model.Items().Single().Content);
    }

    // ---- Reconciling writes --------------------------------------------------------------------

    [Fact]
    public async Task Temp_id_mapping_promotes_the_object_and_clears_the_outbox()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);
        var temp = engine.AddItem(new JsonObject { ["content"] = "New" });

        api.Next = cmds =>
        {
            var add = cmds.Single();
            return Resp("s1",
                changes: [Ch("items", "real1", """{"id":"real1","content":"New","checked":false}""")],
                status: (add.Uuid, true),
                temp: (add.TempId!, "real1"));
        };
        await engine.SyncAsync();

        Assert.Equal(0, engine.PendingCount);
        Assert.Null(engine.Model.Get("items", temp));
        Assert.Equal("real1", engine.Model.Items().Single().Id);
    }

    [Fact]
    public async Task Temp_id_mapping_rewrites_a_queued_child_commands_parent_id()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);
        var parent = engine.AddItem(new JsonObject { ["content"] = "P" });
        engine.AddItem(new JsonObject { ["content"] = "C", ["parent_id"] = parent });

        api.Next = cmds =>
        {
            var parentAdd = cmds.First(c => c.Args["content"]!.ToString() == "P");
            return Resp("s1", status: (parentAdd.Uuid, true), temp: (parent, "realP"));
        };
        await engine.SyncAsync();

        var child = engine.Outbox.Single(c => Args(c)["content"]?.ToString() == "C");
        Assert.Equal("realP", Args(child)["parent_id"]!.ToString());
        Assert.Equal("realP", engine.Model.Items().Single(i => i.Content == "C").ParentId);
    }

    [Fact]
    public async Task An_acked_local_edit_yields_to_the_server_value()
    {
        var api = new FakeApi();
        var engine = SeededEngineAsync(api, out _);
        await engine.SyncAsync();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" });
        api.Next = cmds => Resp("s2",
            changes: [Ch("items", "i1", """{"id":"i1","content":"SERVER"}""")],
            status: (cmds.Single().Uuid, true));
        await engine.SyncAsync();

        Assert.Equal("SERVER", engine.Model.Items().Single().Content);
    }

    [Fact]
    public async Task An_incoming_change_does_not_clobber_an_unacked_local_edit()
    {
        var api = new FakeApi();
        var engine = SeededEngineAsync(api, out _);
        await engine.SyncAsync();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" });
        api.Next = _ => Resp("s2", changes: [Ch("items", "i1", """{"id":"i1","content":"SERVER"}""")]);
        await engine.SyncAsync();

        Assert.Equal("LOCAL", engine.Model.Items().Single().Content);
    }

    [Fact]
    public async Task A_tombstone_withheld_by_a_pending_write_is_applied_once_that_write_resolves()
    {
        var api = new FakeApi();
        var engine = SeededEngineAsync(api, out _);
        await engine.SyncAsync();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" });

        // The server deletes the item while our edit is still un-acked: the tombstone must be held.
        api.Next = _ => Resp("s2", changes: [ChDeleted("items", "i1")]);
        await engine.SyncAsync();
        Assert.Single(engine.Model.Items());

        api.Next = cmds => Resp("s3", status: (cmds.Single().Uuid, true));
        await engine.SyncAsync();

        Assert.Empty(engine.Model.Items());
    }

    [Fact]
    public async Task A_withheld_tombstone_survives_a_restart()
    {
        var store = new InMemorySnapshotStore();
        var api = new FakeApi();
        var secrets = new FakeSecrets { Stored = "tok" };

        var first = new SyncEngine(api, store, secrets);
        api.Next = _ => Resp("s1", changes: [Ch("items", "i1", """{"id":"i1","content":"A"}""")]);
        await first.SyncAsync();
        first.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" });
        api.Next = _ => Resp("s2", changes: [ChDeleted("items", "i1")]);
        await first.SyncAsync();
        Assert.Single(first.Model.Items());

        // Restart before the pending write resolved: the held deletion must still be waiting.
        var reloaded = new SyncEngine(api, store, secrets);
        reloaded.Load();
        api.Next = cmds => Resp("s3", status: (cmds.Single().Uuid, true));
        await reloaded.SyncAsync();

        Assert.Empty(reloaded.Model.Items());
    }

    [Fact]
    public async Task Failed_create_cascades_through_a_whole_offline_subtree()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);
        var parent = engine.AddItem(new JsonObject { ["content"] = "P" });
        var child = engine.AddItem(new JsonObject { ["content"] = "C", ["parent_id"] = parent });
        engine.AddItem(new JsonObject { ["content"] = "G", ["parent_id"] = child });
        Assert.Equal(3, engine.PendingCount);

        api.Next = cmds =>
        {
            var parentAdd = cmds.First(c => c.Args["content"]!.ToString() == "P");
            return Resp("s1", status: (parentAdd.Uuid, false));
        };
        await engine.SyncAsync();

        Assert.Equal(0, engine.PendingCount);
        Assert.Empty(engine.Model.Items());
    }

    [Fact]
    public async Task A_command_that_keeps_failing_reaches_the_poison_ceiling()
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets(), attemptCeiling: 2);

        api.Next = _ => Resp("s1", changes: [Ch("items", "i1", """{"id":"i1","content":"A"}""")]);
        await engine.SyncAsync();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "B" });
        var uuid = engine.Outbox.Single(c => c.Type == "item_update").Uuid;
        api.Next = _ => Resp("s2", status: (uuid, false));

        await engine.SyncAsync();
        Assert.Equal(1, engine.PendingCount);
        Assert.Equal(0, engine.FailedCount);

        await engine.SyncAsync();
        Assert.Equal(0, engine.PendingCount);
        Assert.Equal(1, engine.FailedCount);
    }

    [Fact]
    public async Task A_command_the_server_never_reports_on_eventually_fails()
    {
        var api = new FakeApi { Next = _ => Resp("s1") };
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets(), attemptCeiling: 2);
        engine.AddItem(new JsonObject { ["content"] = "Orphan" });

        await engine.SyncAsync();
        Assert.Equal(1, engine.PendingCount);

        await engine.SyncAsync();

        Assert.Equal(0, engine.PendingCount);
        Assert.Equal(1, engine.FailedCount);
    }

    [Fact]
    public async Task Only_the_first_hundred_pending_commands_flush_per_sync()
    {
        var api = new FakeApi { Next = _ => Resp("s1") };
        var engine = NewEngine(api);
        for (var i = 0; i < 101; i++)
            engine.AddItem(new JsonObject { ["content"] = $"Task {i}" });

        await engine.SyncAsync();

        Assert.Equal(100, api.LastCommands.Count);
        Assert.Equal(101, engine.PendingCount);
    }

    // ---- Fidelity and durability ---------------------------------------------------------------

    [Fact]
    public async Task Field_level_update_preserves_unknown_fields_and_sends_only_the_change()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);
        api.Next = _ => Resp("s1", changes:
        [
            Ch("items", "i1", """{"id":"i1","content":"A","priority":1,"weird_field":"keep"}"""),
        ]);
        await engine.SyncAsync();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "B" });

        var obj = engine.Model.Get("items", "i1")!;
        Assert.Equal("keep", obj["weird_field"]!.ToString());
        Assert.Equal("B", obj["content"]!.ToString());
        Assert.Equal("1", obj["priority"]!.ToString());

        var args = Args(engine.Outbox.Single(c => c.Type == "item_update"));
        Assert.Equal(new[] { "content", "id" }, args.Select(kv => kv.Key).OrderBy(k => k).ToArray());
    }

    [Fact]
    public void Optimistic_edit_survives_a_reload_from_the_store()
    {
        var store = new InMemorySnapshotStore();
        var first = new SyncEngine(new FakeApi(), store, new FakeSecrets());
        first.AddItem(new JsonObject { ["content"] = "Draft" });

        var reloaded = new SyncEngine(new FakeApi(), store, new FakeSecrets());
        reloaded.Load();

        Assert.Equal(1, reloaded.PendingCount);
        Assert.Equal("Draft", reloaded.Model.Items().Single().Content);
    }

    [Fact]
    public async Task Auth_rejection_clears_the_token_and_purges_the_cache()
    {
        var secrets = new FakeSecrets { Stored = "tok" };
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Private"}""");
        var engine = new SyncEngine(new FakeApi { Throw = new TodoistAuthException("no") }, store, secrets);
        engine.Load();

        await Assert.ThrowsAsync<TodoistAuthException>(() => engine.SyncAsync());

        Assert.Null(secrets.Stored);
        Assert.Empty(engine.Model.Items());
        Assert.Empty(store.Load().Resources);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static SyncEngine NewEngine(FakeApi api)
        => new(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" });

    /// <summary>An engine holding one cached item "i1" with content "A", and no pending commands.</summary>
    private static SyncEngine SeededEngine(out InMemorySnapshotStore store)
    {
        store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"A","checked":false}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    /// <summary>An engine whose first sync will deliver item "i1" with content "A".</summary>
    private static SyncEngine SeededEngineAsync(FakeApi api, out InMemorySnapshotStore store)
    {
        store = new InMemorySnapshotStore();
        api.Next = _ => Resp("s1", changes: [Ch("items", "i1", """{"id":"i1","content":"A"}""")]);
        return new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
    }

    private static JsonObject Args(OutboxCommand cmd) => (JsonObject)JsonNode.Parse(cmd.ArgsJson)!;

    private static ResourceChange Ch(string type, string id, string json)
        => new(type, id, false, (JsonObject)JsonNode.Parse(json)!);

    private static ResourceChange ChDeleted(string type, string id)
        => new(type, id, true, new JsonObject { ["id"] = id });

    private static SyncResponse Resp(
        string? token,
        IReadOnlyList<ResourceChange>? changes = null,
        (string Uuid, bool Ok)? status = null,
        (string Temp, string Real)? temp = null)
        => new()
        {
            SyncToken = token,
            Changes = changes ?? [],
            SyncStatus = status is { } s
                ? new Dictionary<string, CommandResult> { [s.Uuid] = new(s.Ok, null, s.Ok ? null : "err") }
                : new Dictionary<string, CommandResult>(),
            TempIdMapping = temp is { } t
                ? new Dictionary<string, string> { [t.Temp] = t.Real }
                : new Dictionary<string, string>(),
        };
}
