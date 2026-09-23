using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// What ticking off a task does to the sub-tasks under it, and what putting it back does.
/// </summary>
/// <remarks>
/// The server finishes a task's sub-tasks along with it and says nothing until the next sync. When
/// it reopens one, it brings back the task's parents but not its sub-tasks. So the client has to
/// do both halves itself: follow the close down the tree, and on the way back, reopen what the
/// close took with it.
/// </remarks>
public class ClosingAParentTests
{
    // ---- Ticking it off ------------------------------------------------------------------------

    [Fact]
    public void Its_open_sub_tasks_are_ticked_off_with_it_however_deep()
    {
        var engine = NewEngine(Family());

        engine.CompleteItem("a");

        var items = Items(engine);
        Assert.All(["a", "b", "c"], id => Assert.True(items[id].Completed));
        Assert.False(items["n"].Completed);
    }

    [Fact]
    public void It_says_which_sub_tasks_went_with_it()
    {
        // The one already finished didn't go with it — it had gone already, and putting the parent
        // back is no reason to bring it back too.
        var engine = NewEngine(Family());

        Assert.Equal(["b", "c"], engine.CompleteItem("a"));
    }

    [Fact]
    public void A_task_with_nothing_under_it_takes_nothing_with_it()
    {
        var engine = NewEngine(Family());

        var under = engine.CompleteItem("n");

        // Ticked off, with nothing under it — which isn't the same answer as a task that wasn't.
        Assert.NotNull(under);
        Assert.Empty(under);
    }

    [Fact]
    public void Only_the_one_command_is_sent_for_the_lot()
    {
        // The server finishes the sub-tasks itself. A close of their own would be a second one for
        // each, which the server has no reason to accept.
        var engine = NewEngine(Family());

        engine.CompleteItem("a");

        var close = Assert.Single(engine.Outbox);
        Assert.Equal("item_close", close.Type);
    }

    [Fact]
    public void A_recurring_task_leaves_its_sub_tasks_alone()
    {
        // The server moves it on to its next date rather than finishing it, and what's under it
        // stays as it was.
        var store = Family();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1,"due":{"date":"2026-07-31","is_recurring":true,"string":"every day"}}""");
        var engine = NewEngine(store);

        // Null rather than empty: the task itself wasn't ticked off here either.
        Assert.Null(engine.CompleteItem("a"));

        var items = Items(engine);
        Assert.False(items["b"].Completed);
        Assert.False(items["c"].Completed);
    }

    // ---- Until the server has it ---------------------------------------------------------------

    [Fact]
    public async Task A_close_the_server_refuses_puts_its_sub_tasks_back_as_well()
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();
        engine.CompleteItem("a");

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        // The server never finished them, and won't resend what never changed there.
        var items = Items(engine);
        Assert.All(["a", "b", "c"], id => Assert.False(items[id].Completed));
    }

    [Fact]
    public async Task A_sub_task_ticked_off_by_a_pending_close_isnt_reopened_by_the_server()
    {
        // The server hasn't taken the close yet, so anything it says about the sub-task meanwhile
        // still has it open. Taken, that would put it back on the list with its parent gone.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 5);
        engine.Load();
        engine.CompleteItem("a");

        // No verdict on the close, so it's still pending once this lands.
        api.Next = _ => new SyncResponse
        {
            SyncToken = "s2",
            Changes = [Json.Change("items", "b", """{"id":"b","content":"B","project_id":"p","parent_id":"a","child_order":1}""")],
        };
        await engine.SyncAsync();

        Assert.Equal(1, engine.PendingCount);
        Assert.True(Items(engine)["b"].Completed);
    }

    [Fact]
    public async Task A_sub_task_the_server_names_while_its_close_is_pending_isnt_reopened()
    {
        // Added offline and ticked off with the task. The add lands first, and the server's copy of
        // the sub-task comes back under its new name, still open.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 5);
        engine.Load();

        var temp = engine.AddItem(new JsonObject { ["content"] = "Added offline", ["project_id"] = "p", ["parent_id"] = "a" });
        Assert.Contains(temp, engine.CompleteItem("a")!);

        // No verdict on the close, so it's still pending once this lands.
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.Where(c => c.Type == "item_add").ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = "real" },
            Changes = [Json.Change("items", "real", """{"id":"real","content":"Added offline","project_id":"p","parent_id":"a","child_order":3}""")],
        };
        await engine.SyncAsync();

        Assert.Equal(1, engine.PendingCount);
        Assert.True(Items(engine)["real"].Completed);
    }

    [Fact]
    public async Task A_refused_close_leaves_an_edit_made_since()
    {
        // It takes the tick off where the task stands. Put back whole, the rename the server took
        // would be undone here and nowhere else.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();
        engine.CompleteItem("a");
        engine.UpdateItem("a", new JsonObject { ["content"] = "Renamed" });

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(
                c => c.Uuid,
                c => c.Type == "item_close" ? new CommandResult(false, "ERR", "rejected") : new CommandResult(true, null, null)),
        };
        await engine.SyncAsync();

        var a = Items(engine)["a"];
        Assert.False(a.Completed);
        Assert.Equal("Renamed", a.Content);
    }

    [Fact]
    public async Task A_refused_close_doesnt_bring_back_a_sub_task_under_the_name_it_had_offline()
    {
        // Added offline under the task, then ticked off with it. The server takes the add and names
        // the sub-task, and refuses the close. Put back whole, the close's copy would come back
        // under the old name, beside the one the server named — two of it, for good.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();

        var temp = engine.AddItem(new JsonObject { ["content"] = "Added offline", ["project_id"] = "p", ["parent_id"] = "a" });
        Assert.Contains(temp, engine.CompleteItem("a")!);

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(
                c => c.Uuid,
                c => c.Type == "item_close" ? new CommandResult(false, "ERR", "rejected") : new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = "real" },
        };
        await engine.SyncAsync();

        var added = Assert.Single(engine.Snapshot().Items, i => i.Content == "Added offline");
        Assert.Equal("real", added.Id);
        Assert.False(added.Completed);
    }

    // ---- Taking it back ------------------------------------------------------------------------

    [Fact]
    public void Undoing_an_unsent_close_puts_its_sub_tasks_back()
    {
        var engine = NewEngine(Family());
        engine.CompleteItem("a");

        Assert.True(engine.Undo());

        var items = Items(engine);
        Assert.All(["a", "b", "c"], id => Assert.False(items[id].Completed));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task Undoing_a_sent_close_tells_the_server_to_reopen_each_sub_task()
    {
        // The server reopens a task's parents with it, but not what's under it. Each has to be
        // asked for, or they stay finished there while showing open here.
        var (engine, api) = WithApi(Family());
        engine.CompleteItem("a");
        await Flush(engine, api);

        Assert.True(engine.Undo());

        Assert.Equal(["a", "b", "c"], Reopened(engine));
        Assert.All(["a", "b", "c"], id => Assert.False(Items(engine)[id].Completed));
    }

    [Fact]
    public async Task Reopening_the_task_brings_back_what_went_with_it()
    {
        // The box clicked again, which is the other way back and the one most people will use.
        var (engine, api) = WithApi(Family());
        engine.CompleteItem("a");
        await Flush(engine, api);

        engine.ReopenItem("a");

        Assert.Equal(["a", "b", "c"], Reopened(engine));
        Assert.All(["a", "b", "c"], id => Assert.False(Items(engine)[id].Completed));
    }

    [Fact]
    public void A_sub_task_finished_before_the_close_stays_finished()
    {
        var engine = NewEngine(Family());
        engine.CompleteItem("a");

        engine.ReopenItem("a");

        Assert.True(Items(engine)["d"].Completed);
        Assert.DoesNotContain("d", Reopened(engine));
    }

    [Fact]
    public void A_close_queued_before_a_restart_still_knows_what_it_took()
    {
        // The undo stack is rebuilt from the outbox, and the sub-tasks are in the close's priors
        // there — so what went with it isn't something only this session remembered.
        var store = Family();
        NewEngine(store).CompleteItem("a");

        var restarted = NewEngine(store);

        Assert.True(restarted.Undo());

        var items = Items(restarted);
        Assert.All(["a", "b", "c"], id => Assert.False(items[id].Completed));
    }

    [Fact]
    public async Task Undoing_a_close_after_a_restart_reopens_a_sub_task_the_server_named_before_it()
    {
        // Added offline and ticked off with the task, then named by the server while the close
        // waited. After a restart the undo stack comes from the close's own record, so that has to
        // have the new name.
        var store = Family();
        var (engine, api) = WithApi(store);

        var temp = engine.AddItem(new JsonObject { ["content"] = "Added offline", ["project_id"] = "p", ["parent_id"] = "a" });
        engine.CompleteItem("a");

        // No verdict on the close, so it's still queued.
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.Where(c => c.Type == "item_add").ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = "real" },
        };
        await engine.SyncAsync();

        var restarted = NewEngine(store);

        Assert.True(restarted.Undo());
        Assert.False(Items(restarted)["real"].Completed);
    }

    [Fact]
    public void Reopening_a_sub_task_reopens_its_parents_without_asking_the_server_to()
    {
        // The server brings back a reopened task's parents by itself. Left finished here, the
        // sub-task would sit on the list with no parent in view until the next sync.
        var engine = NewEngine(Family());
        engine.CompleteItem("a");

        engine.ReopenItem("c");

        var items = Items(engine);
        Assert.False(items["a"].Completed);
        Assert.False(items["b"].Completed);
        Assert.False(items["c"].Completed);
        Assert.Equal(["c"], Reopened(engine));
    }

    [Fact]
    public void A_sub_task_ticked_off_on_its_own_first_isnt_brought_back_with_the_parent()
    {
        // The same as one finished long ago, but by this session: the parent's close found it
        // finished already and left it, so putting the parent back leaves it where it was.
        var engine = NewEngine(Family());
        engine.CompleteItem("c");
        engine.CompleteItem("a");

        engine.ReopenItem("a");

        var items = Items(engine);
        Assert.False(items["a"].Completed);
        Assert.False(items["b"].Completed);
        Assert.True(items["c"].Completed);
    }

    // ---- Putting it back uses the close up -----------------------------------------------------

    [Fact]
    public void A_close_already_taken_back_isnt_used_again()
    {
        // Ticked off with its sub-task, put back, and then the sub-task finished on purpose and
        // the lot closed from further up. Reopening the task again is about the task: the first
        // close's list is spent, and using it would bring back a sub-task nobody asked for.
        var engine = NewEngine(Family());
        engine.CompleteItem("b");
        engine.ReopenItem("b");

        engine.CompleteItem("c");
        engine.CompleteItem("a");

        engine.ReopenItem("b");

        var items = Items(engine);
        Assert.False(items["b"].Completed);
        Assert.True(items["c"].Completed);
    }

    [Fact]
    public async Task Undo_after_putting_a_task_back_leaves_it_alone()
    {
        // The tick has been taken back already. A second go would drop nothing and send nothing
        // useful — and with the close gone, a reopen of a task the server never closed.
        var (engine, api) = WithApi(Family());
        engine.CompleteItem("a");
        await Flush(engine, api);
        engine.ReopenItem("a");

        Assert.False(engine.Undo());

        Assert.Equal(["a", "b", "c"], Reopened(engine));
        Assert.All(["a", "b", "c"], id => Assert.False(Items(engine)[id].Completed));
    }

    [Fact]
    public void Undo_after_putting_back_a_close_that_never_went_leaves_nothing_queued()
    {
        var engine = NewEngine(Family());
        engine.CompleteItem("a");
        engine.ReopenItem("a");

        Assert.False(engine.Undo());

        Assert.Empty(engine.Outbox);
        Assert.All(["a", "b", "c"], id => Assert.False(Items(engine)[id].Completed));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>The tasks the outbox is asking the server to reopen, in the order it'll ask.</summary>
    private static List<string> Reopened(SyncEngine engine)
        => engine.Outbox
            .Where(c => c.Type == "item_uncomplete")
            .Select(c => JsonNode.Parse(c.ArgsJson)!["id"]!.ToString())
            .ToList();

    private static Dictionary<string, TaskItem> Items(SyncEngine engine)
        => engine.Snapshot().Items.ToDictionary(i => i.Id);

    /// <summary>Sends what is queued and has the server accept all of it.</summary>
    private static async Task Flush(SyncEngine engine, FakeApi api)
    {
        api.Next = cmds => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };

        await engine.SyncAsync();
        Assert.Equal(0, engine.PendingCount);
    }

    private static Dictionary<string, CommandResult> Refused(IReadOnlyList<Command> commands)
        => commands.ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected"));

    private static SyncEngine NewEngine(InMemorySnapshotStore store) => WithApi(store).Engine;

    private static (SyncEngine Engine, FakeApi Api) WithApi(InMemorySnapshotStore store)
    {
        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(new DateOnly(2026, 7, 31)));
        engine.Load();
        return (engine, api);
    }

    /// <summary>
    /// A task with a sub-task and a sub-sub-task open under it, one sub-task already finished, and
    /// a task beside it that's none of theirs.
    /// </summary>
    private static InMemorySnapshotStore Family()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","parent_id":"b","child_order":1}""");
        store.PutResource("items", "d", """{"id":"d","content":"D","project_id":"p","parent_id":"a","child_order":2,"checked":true}""");
        store.PutResource("items", "n", """{"id":"n","content":"N","project_id":"p","child_order":2}""");
        return store;
    }
}
