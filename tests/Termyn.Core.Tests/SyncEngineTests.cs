using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

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

        Assert.Equal("s1", engine.SyncToken);
        Assert.Equal("A", engine.Snapshot().Items.Single().Content);
        Assert.Equal("Work", engine.Snapshot().Projects.Single().Name);
    }

    [Fact]
    public async Task Reconcile_removes_a_tombstoned_resource()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);

        api.Next = _ => Resp("s1", changes: [Ch("items", "i1", """{"id":"i1","content":"A"}""")]);
        await engine.SyncAsync();
        Assert.Single(engine.Snapshot().Items);

        api.Next = _ => Resp("s2", changes: [Json.Deleted("items", "i1")]);
        await engine.SyncAsync();
        Assert.Empty(engine.Snapshot().Items);
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

        Assert.Equal("s1", engine.SyncToken);
    }

    [Fact]
    public void Load_skips_unparseable_rows_instead_of_failing()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "good", """{"id":"good","content":"A"}""");
        store.PutResource("items", "bad", "{ this is not json");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets());

        engine.Load();

        Assert.Equal("A", engine.Snapshot().Items.Single().Content);
    }

    // ---- Capture -------------------------------------------------------------------------------

    [Fact]
    public async Task Quick_add_folds_the_server_created_task_into_the_model()
    {
        var api = new FakeApi
        {
            QuickAdd = text => Json.Change("items", "srv1", $$"""{"id":"srv1","content":"{{text}}","priority":4}"""),
        };
        var engine = NewEngine(api);

        Assert.True(await engine.QuickAddOnlineAsync("Email report tomorrow"));

        var item = engine.Snapshot().Items.Single();
        Assert.Equal("Email report tomorrow", item.Content);
        Assert.Equal(Priority.P1, item.Priority); // the server resolved it, API 4 -> P1
        Assert.Equal(0, engine.PendingCount);     // nothing queued: it already exists server-side
    }

    [Fact]
    public async Task Quick_add_reports_failure_when_offline_so_the_caller_can_fall_back()
    {
        var engine = NewEngine(new FakeApi()); // QuickAdd unset = unreachable

        Assert.False(await engine.QuickAddOnlineAsync("Buy milk"));
        Assert.Empty(engine.Snapshot().Items);
    }

    [Fact]
    public async Task Quick_add_clears_the_token_when_rejected()
    {
        var secrets = new FakeSecrets { Stored = "tok" };
        var engine = new SyncEngine(new FakeApi { Throw = new TodoistAuthException("no") }, new InMemorySnapshotStore(), secrets);

        await Assert.ThrowsAsync<TodoistAuthException>(() => engine.QuickAddOnlineAsync("Buy milk"));
        Assert.Null(secrets.Stored);
    }

    // ---- Optimistic writes ---------------------------------------------------------------------

    [Fact]
    public void Optimistic_add_queues_a_command_and_shows_immediately()
    {
        var engine = NewEngine(new FakeApi());

        var temp = engine.AddItem(new JsonObject { ["content"] = "New" });

        Assert.Equal(1, engine.PendingCount);
        Assert.Equal("New", engine.Snapshot().Items.Single().Content);
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
        var engine = SeededEngine();

        engine.CompleteItem("i1");

        Assert.True(engine.Snapshot().Items.Single().Completed);
        var cmd = engine.Outbox.Single();
        Assert.Equal("item_close", cmd.Type);
        Assert.Equal("i1", Args(cmd)["id"]!.ToString());
    }

    [Fact]
    public void Reopen_clears_the_check_and_queues_item_uncomplete()
    {
        var engine = SeededEngine();
        engine.CompleteItem("i1");

        engine.ReopenItem("i1");

        Assert.False(engine.Snapshot().Items.Single().Completed);
        Assert.Equal("item_uncomplete", engine.Outbox.Last().Type);
    }

    [Fact]
    public void Delete_removes_the_item_optimistically_and_queues_item_delete()
    {
        var engine = SeededEngine();

        engine.DeleteItem("i1");

        Assert.Empty(engine.Snapshot().Items);
        var cmd = engine.Outbox.Single();
        Assert.Equal("item_delete", cmd.Type);
        Assert.Equal("i1", Args(cmd)["id"]!.ToString());
    }

    [Fact]
    public void Update_of_an_item_we_no_longer_hold_queues_nothing()
    {
        var engine = NewEngine(new FakeApi());

        engine.UpdateItem("ghost", new JsonObject { ["content"] = "B" });

        // The task is already gone; the command could only fail until it poisoned.
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Acting_on_an_unknown_id_offers_no_meaningless_undo()
    {
        var engine = NewEngine(new FakeApi());

        engine.DeleteItem("ghost");
        engine.CompleteItem("ghost");

        Assert.False(engine.CanUndo);
    }

    [Fact]
    public async Task Sync_without_a_stored_token_throws_before_calling_the_api()
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = null });

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SyncAsync());
        Assert.Empty(api.LastCommands);
    }

    // ---- Reorder -------------------------------------------------------------------------------

    [Fact]
    public void Reorder_renumbers_consecutively_in_the_new_order()
    {
        var engine = OrderedEngine();

        engine.ReorderItems(["b", "a"]);

        var cmd = engine.Outbox.Single();
        Assert.Equal("item_reorder", cmd.Type);
        var entries = Args(cmd)["items"]!.AsArray();
        Assert.Equal("b", entries[0]!["id"]!.ToString());
        Assert.Equal("1", entries[0]!["child_order"]!.ToString());
        Assert.Equal("a", entries[1]!["id"]!.ToString());
        Assert.Equal("2", entries[1]!["child_order"]!.ToString());
    }

    [Fact]
    public void Reorder_ignores_ids_it_no_longer_holds()
    {
        var engine = OrderedEngine();

        engine.ReorderItems(["b", "ghost", "a"]);

        var entries = Args(engine.Outbox.Single())["items"]!.AsArray();
        Assert.Equal(new[] { "b", "a" }, entries.Select(e => e!["id"]!.ToString()).ToArray());
        Assert.Equal(new[] { "1", "2" }, entries.Select(e => e!["child_order"]!.ToString()).ToArray());
    }

    [Fact]
    public void Reordering_nothing_queues_nothing()
    {
        var engine = OrderedEngine();

        engine.ReorderItems([]);
        engine.ReorderItems(["ghost"]);

        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Moving_a_task_only_renumbers_its_own_siblings()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a1", """{"id":"a1","content":"A1","project_id":"pA","child_order":1}""");
        store.PutResource("items", "a2", """{"id":"a2","content":"A2","project_id":"pA","child_order":2}""");
        store.PutResource("items", "b1", """{"id":"b1","content":"B1","project_id":"pB","child_order":1}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.MoveItem("a2", -1);

        var entries = Args(engine.Outbox.Single())["items"]!.AsArray();
        Assert.Equal(new[] { "a2", "a1" }, entries.Select(e => e!["id"]!.ToString()).ToArray());

        // The other project is untouched.
        Assert.Equal(1, engine.Snapshot().Items.Single(i => i.Id == "b1").ChildOrder);
    }

    [Fact]
    public void Moving_a_subtask_leaves_its_parents_peers_alone()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "t1", """{"id":"t1","content":"T1","project_id":"p","child_order":1}""");
        store.PutResource("items", "t2", """{"id":"t2","content":"T2","project_id":"p","child_order":2}""");
        store.PutResource("items", "c1", """{"id":"c1","content":"C1","project_id":"p","parent_id":"t1","child_order":1}""");
        store.PutResource("items", "c2", """{"id":"c2","content":"C2","project_id":"p","parent_id":"t1","child_order":2}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        Assert.True(engine.MoveItem("c2", -1));

        var entries = Args(engine.Outbox.Single())["items"]!.AsArray();
        Assert.Equal(new[] { "c2", "c1" }, entries.Select(e => e!["id"]!.ToString()).ToArray());

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.Equal(1, items["t1"].ChildOrder);
        Assert.Equal(2, items["t2"].ChildOrder);
    }

    [Fact]
    public void Moving_steps_over_a_completed_sibling_that_is_not_on_screen()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "x", """{"id":"x","content":"X","project_id":"p","child_order":1}""");
        store.PutResource("items", "done", """{"id":"done","content":"Done","project_id":"p","checked":true,"child_order":2}""");
        store.PutResource("items", "y", """{"id":"y","content":"Y","project_id":"p","child_order":3}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        // One press should put Y above X, not swap it with the invisible completed task.
        Assert.True(engine.MoveItem("y", -1));

        var active = engine.Snapshot().Items.Where(i => !i.Completed).OrderBy(i => i.ChildOrder).Select(i => i.Id);
        Assert.Equal(new[] { "y", "x" }, active.ToArray());
    }

    [Fact]
    public void Only_the_tasks_whose_position_changed_are_sent()
    {
        var store = new InMemorySnapshotStore();
        for (var i = 1; i <= 6; i++)
            store.PutResource("items", $"i{i}", $$"""{"id":"i{{i}}","content":"T{{i}}","project_id":"p","child_order":{{i}}}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.MoveItem("i5", -1);

        // Swapping two adjacent tasks must not rewrite the whole project.
        var entries = Args(engine.Outbox.Single())["items"]!.AsArray();
        Assert.Equal(new[] { "i5", "i4" }, entries.Select(e => e!["id"]!.ToString()).ToArray());
    }

    [Fact]
    public void Reordering_the_same_id_twice_keeps_the_first_position()
    {
        var engine = OrderedEngine(); // a at 1, b at 2

        engine.ReorderItems(["b", "a", "b"]);

        // The repeat is ignored rather than pushing b to the end.
        var entries = Args(engine.Outbox.Single())["items"]!.AsArray();
        Assert.Equal(new[] { "b", "a" }, entries.Select(e => e!["id"]!.ToString()).ToArray());
        Assert.Equal(1, engine.Snapshot().Items.Single(i => i.Id == "b").ChildOrder);
        Assert.Equal(2, engine.Snapshot().Items.Single(i => i.Id == "a").ChildOrder);
    }

    [Fact]
    public void Reverting_a_reorder_restores_every_position()
    {
        var store = new InMemorySnapshotStore();
        for (var i = 1; i <= 3; i++)
            store.PutResource("items", $"i{i}", $$"""{"id":"i{{i}}","content":"T{{i}}","project_id":"p","child_order":{{i}}}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.MoveItem("i3", -2);
        engine.Revert(engine.Outbox.Single().Uuid);

        var orders = engine.Snapshot().Items.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i.ChildOrder);
        Assert.Equal(new[] { 1, 2, 3 }, orders.ToArray());
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task A_pending_reorder_defers_a_tombstone_for_one_of_its_tasks()
    {
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.MoveItem("b", -1);

        // The server deletes one of the tasks being reordered, before the reorder has flushed.
        api.Next = _ => Resp("s1", changes: [Json.Deleted("items", "b")]);
        await engine.SyncAsync();

        // Applying it now would leave the queued reorder pointing at a task that no longer exists.
        Assert.Contains(engine.Snapshot().Items, i => i.Id == "b");
    }

    [Fact]
    public void Moving_down_steps_over_a_completed_sibling()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "x", """{"id":"x","content":"X","project_id":"p","child_order":1}""");
        store.PutResource("items", "done", """{"id":"done","content":"Done","project_id":"p","checked":true,"child_order":2}""");
        store.PutResource("items", "y", """{"id":"y","content":"Y","project_id":"p","child_order":3}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        Assert.True(engine.MoveItem("x", 1));

        var active = engine.Snapshot().Items.Where(i => !i.Completed).OrderBy(i => i.ChildOrder).Select(i => i.Id);
        Assert.Equal(new[] { "y", "x" }, active.ToArray());
    }

    [Fact]
    public void A_task_with_only_completed_siblings_below_it_cannot_move_down()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "x", """{"id":"x","content":"X","project_id":"p","child_order":1}""");
        store.PutResource("items", "y", """{"id":"y","content":"Y","project_id":"p","child_order":2}""");
        store.PutResource("items", "d1", """{"id":"d1","content":"D1","project_id":"p","checked":true,"child_order":3}""");
        store.PutResource("items", "d2", """{"id":"d2","content":"D2","project_id":"p","checked":true,"child_order":4}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        Assert.False(engine.MoveItem("y", 1));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Reordering_into_the_order_they_are_already_in_queues_nothing()
    {
        var engine = OrderedEngine();

        engine.ReorderItems(["a", "b"]);

        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Moving_past_either_end_does_nothing()
    {
        var engine = OrderedEngine();

        Assert.False(engine.MoveItem("a", -1));
        Assert.False(engine.MoveItem("b", 1));
        Assert.Equal(0, engine.PendingCount);
    }

    // ---- Undo ----------------------------------------------------------------------------------

    [Fact]
    public void Undo_of_an_unflushed_completion_restores_the_task()
    {
        var engine = SeededEngine();
        engine.CompleteItem("i1");
        Assert.True(engine.CanUndo);

        Assert.True(engine.Undo());

        Assert.False(engine.Snapshot().Items.Single().Completed);
        Assert.Equal(0, engine.PendingCount); // the queued command went with it
    }

    [Fact]
    public void Undo_of_an_unflushed_delete_restores_the_task()
    {
        var engine = SeededEngine();
        engine.DeleteItem("i1");

        Assert.True(engine.Undo());

        Assert.Equal("A", engine.Snapshot().Items.Single().Content);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task Undo_of_an_acked_completion_issues_the_opposite_command()
    {
        var api = new FakeApi();
        var engine = SeededEngine(api);
        engine.CompleteItem("i1");

        api.Next = cmds => Resp("s1", status: (cmds.Single().Uuid, true));
        await engine.SyncAsync();
        Assert.Equal(0, engine.PendingCount);

        Assert.True(engine.Undo());

        Assert.Equal("item_uncomplete", engine.Outbox.Single().Type);
        Assert.False(engine.Snapshot().Items.Single().Completed);
    }

    [Fact]
    public async Task Undo_of_an_acked_delete_recreates_the_task_without_server_owned_fields()
    {
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"A","priority":3,"user_id":"u1","added_at":"2026-01-01T00:00:00Z"}""");
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.DeleteItem("i1");
        api.Next = cmds => Resp("s1", status: (cmds.Single().Uuid, true));
        await engine.SyncAsync();

        Assert.True(engine.Undo());

        var add = engine.Outbox.Single(c => c.Type == "item_add");
        var args = Args(add);
        Assert.Equal("A", args["content"]!.ToString());
        Assert.Equal("3", args["priority"]!.ToString());
        Assert.DoesNotContain("user_id", args.Select(kv => kv.Key));
        Assert.DoesNotContain("added_at", args.Select(kv => kv.Key));
        Assert.DoesNotContain("id", args.Select(kv => kv.Key));
    }

    [Fact]
    public async Task Undo_after_a_temp_id_is_promoted_targets_the_real_id()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);
        var temp = engine.AddItem(new JsonObject { ["content"] = "New" });
        engine.CompleteItem(temp);

        api.Next = cmds => new SyncResponse
        {
            SyncToken = "s1",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = "real1" },
        };
        await engine.SyncAsync();

        Assert.True(engine.Undo());

        var cmd = engine.Outbox.Single(c => c.Type == "item_uncomplete");
        Assert.Equal("real1", Args(cmd)["id"]!.ToString());
    }

    [Fact]
    public async Task Undo_will_not_drop_a_command_that_is_already_on_the_wire()
    {
        var gate = new TaskCompletionSource();
        var api = new FakeApi();
        var engine = SeededEngine(api);
        engine.CompleteItem("i1");

        // Hold the response open so the command is genuinely in flight while we undo.
        api.Next = cmds =>
        {
            gate.TrySetResult();
            Thread.Sleep(50);
            return Resp("s1", status: (cmds.Single().Uuid, true));
        };
        var syncing = Task.Run(() => engine.SyncAsync());
        await gate.Task;

        var undone = engine.Undo();
        await syncing;

        // Undo must compensate rather than silently dropping a command the server is applying.
        Assert.True(undone);
        Assert.Equal("item_uncomplete", engine.Outbox.Single().Type);
    }

    [Fact]
    public async Task Undo_skips_past_a_write_whose_task_has_since_been_deleted()
    {
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"One"}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Two"}""");
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.CompleteItem("i1");
        engine.CompleteItem("i2");
        api.Next = cmds => new SyncResponse
        {
            SyncToken = "s1",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };
        await engine.SyncAsync();

        // Another device deletes i2, the most recent undo target.
        api.Next = _ => Resp("s2", changes: [Json.Deleted("items", "i2")]);
        await engine.SyncAsync();

        // Undo should fall through to i1 rather than reporting there is nothing to undo.
        Assert.True(engine.Undo());
        Assert.False(engine.Snapshot().Items.Single(i => i.Id == "i1").Completed);
    }

    [Fact]
    public async Task Undo_skips_a_delete_it_could_only_recreate_as_a_blank_task()
    {
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","priority":2}"""); // no content
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.DeleteItem("i1");
        api.Next = cmds => Resp("s1", status: (cmds.Single().Uuid, true));
        await engine.SyncAsync();

        Assert.False(engine.Undo());
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void An_unflushed_delete_is_still_undoable_after_a_restart()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"A"}""");
        var first = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        first.Load();
        first.DeleteItem("i1");

        var reloaded = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        reloaded.Load();

        Assert.True(reloaded.CanUndo);
        Assert.True(reloaded.Undo());
        Assert.Equal("A", reloaded.Snapshot().Items.Single().Content);
    }

    [Fact]
    public void The_undo_history_is_bounded()
    {
        var store = new InMemorySnapshotStore();
        for (var i = 0; i < 60; i++)
            store.PutResource("items", $"i{i}", $$"""{"id":"i{{i}}","content":"T{{i}}"}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        for (var i = 0; i < 60; i++)
            engine.CompleteItem($"i{i}");

        var undone = 0;
        while (engine.Undo())
            undone++;

        Assert.Equal(50, undone);
        Assert.False(engine.CanUndo);
    }

    [Fact]
    public void Reverting_a_write_takes_it_out_of_the_undo_history()
    {
        var engine = SeededEngine();
        engine.CompleteItem("i1");

        engine.Revert(engine.Outbox.Single().Uuid);

        Assert.False(engine.CanUndo);
    }

    [Fact]
    public void Undo_reverses_in_reverse_order_and_then_reports_nothing_left()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"One"}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Two"}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.CompleteItem("i1");
        engine.DeleteItem("i2");

        Assert.True(engine.Undo());
        Assert.Contains(engine.Snapshot().Items, i => i.Id == "i2");
        Assert.True(engine.Snapshot().Items.Single(i => i.Id == "i1").Completed);

        Assert.True(engine.Undo());
        Assert.False(engine.Snapshot().Items.Single(i => i.Id == "i1").Completed);

        Assert.False(engine.CanUndo);
        Assert.False(engine.Undo());
    }

    [Fact]
    public async Task A_cascade_cancelled_write_leaves_nothing_to_undo()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);
        var temp = engine.AddItem(new JsonObject { ["content"] = "P" });
        engine.CompleteItem(temp);

        api.Next = cmds =>
        {
            var add = cmds.First(c => c.Type == "item_add");
            return Resp("s1", status: (add.Uuid, false));
        };
        await engine.SyncAsync();

        Assert.False(engine.CanUndo);
        Assert.Equal(0, engine.PendingCount);
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
        Assert.Null(engine.RawResource("items", temp));
        Assert.Equal("real1", engine.Snapshot().Items.Single().Id);
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
        Assert.Equal("realP", engine.Snapshot().Items.Single(i => i.Content == "C").ParentId);
    }

    [Fact]
    public async Task An_acked_local_edit_yields_to_the_server_value()
    {
        var api = new FakeApi();
        var engine = SeededEngine(api);

        engine.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" });
        api.Next = cmds => Resp("s2",
            changes: [Ch("items", "i1", """{"id":"i1","content":"SERVER"}""")],
            status: (cmds.Single().Uuid, true));
        await engine.SyncAsync();

        Assert.Equal("SERVER", engine.Snapshot().Items.Single().Content);
    }

    [Fact]
    public async Task An_incoming_change_does_not_clobber_an_unacked_local_edit()
    {
        var api = new FakeApi();
        var engine = SeededEngine(api);

        engine.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" });
        api.Next = _ => Resp("s2", changes: [Ch("items", "i1", """{"id":"i1","content":"SERVER"}""")]);
        await engine.SyncAsync();

        Assert.Equal("LOCAL", engine.Snapshot().Items.Single().Content);
    }

    [Fact]
    public async Task A_write_aimed_at_an_item_we_never_held_does_not_shadow_the_server_copy()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);

        // Completing an id nothing is cached for still queues, with no prior state to protect.
        engine.CompleteItem("i1");

        api.Next = _ => Resp("s1", changes: [Ch("items", "i1", """{"id":"i1","content":"SERVER"}""")]);
        await engine.SyncAsync();

        // The token has advanced past this change, so dropping it would lose it permanently.
        Assert.Equal("SERVER", engine.Snapshot().Items.Single().Content);
    }

    [Fact]
    public async Task A_pending_reorder_does_not_swallow_a_sibling_edit_from_another_device()
    {
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.MoveItem("b", -1); // queues a reorder naming both a and b

        // Another device renames A while our reorder is still queued.
        api.Next = _ => Resp("s1", changes: [Ch("items", "a", """{"id":"a","content":"RENAMED","project_id":"p","child_order":1}""")]);
        await engine.SyncAsync();

        // The reorder only owns the position, so the rename must land — the token moved past it.
        var a = engine.Snapshot().Items.Single(i => i.Id == "a");
        Assert.Equal("RENAMED", a.Content);
        Assert.Equal(2, a.ChildOrder); // and our optimistic position survives
    }

    [Fact]
    public async Task A_temp_id_inside_a_queued_reorder_is_remapped()
    {
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "b", """{"id":"b","content":"B","child_order":2}""");
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var temp = engine.AddItem(new JsonObject { ["content"] = "New" });
        engine.MoveItem(temp, 1);

        api.Next = cmds =>
        {
            var add = cmds.First(c => c.Type == "item_add");
            return Resp("s1", status: (add.Uuid, true), temp: (temp, "real1"));
        };
        await engine.SyncAsync();

        var reorder = engine.Outbox.Single(c => c.Type == "item_reorder");
        var ids = Args(reorder)["items"]!.AsArray().Select(e => e!["id"]!.ToString());
        Assert.Contains("real1", ids);
        Assert.DoesNotContain(temp, ids);
    }

    [Fact]
    public async Task A_cancelled_reorder_puts_its_other_tasks_back_where_they_were()
    {
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var temp = engine.AddItem(new JsonObject { ["content"] = "New", ["project_id"] = "p" });
        engine.MoveItem(temp, 1); // renumbers a and b around the new task

        api.Next = cmds =>
        {
            var add = cmds.First(c => c.Type == "item_add");
            return Resp("s1", status: (add.Uuid, false));
        };
        await engine.SyncAsync();

        // The reorder went with the failed create, so the server never hears about it: the local
        // positions must go back rather than silently diverging.
        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.Equal(1, items["a"].ChildOrder);
        Assert.Equal(2, items["b"].ChildOrder);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task A_failed_create_cancels_a_reorder_that_named_it()
    {
        var api = new FakeApi();
        var engine = NewEngine(api);
        var temp = engine.AddItem(new JsonObject { ["content"] = "New" });
        engine.MoveItem(temp, 1);

        api.Next = cmds =>
        {
            var add = cmds.First(c => c.Type == "item_add");
            return Resp("s1", status: (add.Uuid, false));
        };
        await engine.SyncAsync();

        // A reorder naming a task that was never created could only fail forever.
        Assert.Equal(0, engine.PendingCount);
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
        api.Next = _ => Resp("s2", changes: [Json.Deleted("items", "i1")]);
        await first.SyncAsync();
        Assert.Single(first.Snapshot().Items);

        // Restart before the pending write resolved: the held deletion must still be waiting.
        var reloaded = new SyncEngine(api, store, secrets);
        reloaded.Load();
        api.Next = cmds => Resp("s3", status: (cmds.Single().Uuid, true));
        await reloaded.SyncAsync();

        Assert.Empty(reloaded.Snapshot().Items);
    }

    [Fact]
    public async Task A_tombstone_withheld_by_a_pending_write_is_applied_once_that_write_resolves()
    {
        var api = new FakeApi();
        var engine = SeededEngine(api);

        engine.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" });

        api.Next = _ => Resp("s2", changes: [Json.Deleted("items", "i1")]);
        await engine.SyncAsync();
        Assert.Single(engine.Snapshot().Items);

        api.Next = cmds => Resp("s3", status: (cmds.Single().Uuid, true));
        await engine.SyncAsync();

        Assert.Empty(engine.Snapshot().Items);
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
        Assert.Empty(engine.Snapshot().Items);
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

    [Fact]
    public async Task A_response_that_lands_after_the_cache_was_purged_is_discarded()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var api = new FakeApi
        {
            Next = _ =>
            {
                entered.TrySetResult();
                release.Task.Wait();
                return Resp("s1", changes: [Ch("items", "i1", """{"id":"i1","content":"Private"}""")]);
            },
        };
        var store = new InMemorySnapshotStore();
        var secrets = new FakeSecrets { Stored = "tok" };
        var engine = new SyncEngine(api, store, secrets);

        var inFlight = Task.Run(() => engine.SyncAsync());
        await entered.Task;

        // The token is rejected while that response is still coming back, wiping the cache.
        api.Throw = new TodoistAuthException("no");
        await Assert.ThrowsAsync<TodoistAuthException>(() => engine.SyncAsync());
        api.Throw = null;

        release.SetResult();
        await inFlight;

        // The purged account's tasks must not come back with the late response.
        Assert.Empty(engine.Snapshot().Items);
        Assert.Empty(store.Load().Resources);
    }

    [Fact]
    public async Task A_quick_add_that_lands_after_a_purge_does_not_repopulate_the_cache()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var api = new FakeApi
        {
            QuickAdd = _ =>
            {
                entered.TrySetResult();
                release.Task.Wait();
                return Json.Change("items", "srv1", """{"id":"srv1","content":"Private"}""");
            },
        };
        var store = new InMemorySnapshotStore();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });

        var inFlight = Task.Run(() => engine.QuickAddOnlineAsync("Private"));
        await entered.Task;

        api.Throw = new TodoistAuthException("no");
        await Assert.ThrowsAsync<TodoistAuthException>(() => engine.SyncAsync());
        api.Throw = null;

        release.SetResult();
        Assert.True(await inFlight); // it was created server-side

        Assert.Empty(engine.Snapshot().Items);
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

        var obj = engine.RawResource("items", "i1")!;
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
        Assert.Equal("Draft", reloaded.Snapshot().Items.Single().Content);
    }

    [Fact]
    public void A_write_the_store_cannot_persist_leaves_no_trace_in_the_model()
    {
        var store = new FailingWriteStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"A","child_order":1}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"B","child_order":2}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        Assert.Throws<IOException>(() => engine.UpdateItem("i1", new JsonObject { ["content"] = "LOCAL" }));
        Assert.Throws<IOException>(() => engine.AddItem(new JsonObject { ["content"] = "New" }));
        Assert.Throws<IOException>(() => engine.DeleteItem("i1"));
        Assert.Throws<IOException>(() => engine.MoveItem("i2", -1));

        // Nothing was shown that wasn't also durably queued.
        Assert.Equal(0, engine.PendingCount);
        Assert.Equal(new[] { "A", "B" }, engine.Snapshot().Items.OrderBy(i => i.ChildOrder).Select(i => i.Content).ToArray());
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
        Assert.Empty(engine.Snapshot().Items);
        Assert.Empty(store.Load().Resources);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static SyncEngine NewEngine(FakeApi api)
        => new(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" });

    /// <summary>An engine holding one cached item "i1" with content "A", and no pending commands.</summary>
    private static SyncEngine SeededEngine(FakeApi? api = null)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"A","checked":false}""");
        var engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    /// <summary>An engine holding two same-project items "a" then "b".</summary>
    private static SyncEngine OrderedEngine()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","child_order":2}""");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    private static JsonObject Args(OutboxCommand cmd) => (JsonObject)JsonNode.Parse(cmd.ArgsJson)!;

    private static ResourceChange Ch(string type, string id, string json) => Json.Change(type, id, json);

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
