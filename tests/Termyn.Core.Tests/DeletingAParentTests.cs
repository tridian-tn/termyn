using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;
using static Termyn.TestSupport.Flushing;

namespace Termyn.Core.Tests;

/// <summary>
/// What deleting a task does to the sub-tasks under it, and what putting it back does.
/// </summary>
/// <remarks>
/// The server deletes a task's sub-tasks along with it and says nothing until the next sync. Left
/// until then, they sat on the list as top-level tasks with nothing above them. So the client
/// takes them off itself, and brings them back with the task.
/// </remarks>
public class DeletingAParentTests
{
    // ---- Deleting it ---------------------------------------------------------------------------

    [Fact]
    public void Its_sub_tasks_go_with_it_however_deep()
    {
        var engine = NewEngine(Family());

        engine.DeleteItem("a");

        Assert.Equal(["n"], Items(engine).Keys);
    }

    [Fact]
    public void Only_the_one_command_is_sent_for_the_lot()
    {
        // The server deletes the sub-tasks itself. A delete of their own would only be refused.
        var engine = NewEngine(Family());

        engine.DeleteItem("a");

        Assert.Equal(["a"], Asked(engine, "item_delete"));
    }

    [Fact]
    public void A_sub_task_picked_out_with_its_parent_isnt_deleted_a_second_time()
    {
        var engine = NewEngine(Family());

        engine.DeleteItems(["a", "b"]);

        Assert.Equal(["a"], Asked(engine, "item_delete"));
    }

    [Fact]
    public async Task Its_completed_sub_tasks_fetched_from_the_archive_go_with_it()
    {
        var api = new FakeApi
        {
            Response = new SyncResponse { SyncToken = "s1" },
            Completed = _ => new CompletedPage(
                [Json.Change("items", "x", """{"id":"x","content":"X","project_id":"p","parent_id":"n","checked":true}""")],
                null),
        };
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        await engine.FetchCompletedAsync(TestContext.Current.CancellationToken);

        engine.DeleteItem("n");

        Assert.DoesNotContain(engine.Snapshot().CompletedItems, i => i.Id == "x");
    }

    // ---- Putting it back -----------------------------------------------------------------------

    [Fact]
    public void Undoing_it_before_it_goes_brings_its_sub_tasks_back_where_they_were()
    {
        var engine = NewEngine(Family());
        engine.DeleteItem("a");

        Assert.True(engine.Undo());

        var items = Items(engine);
        Assert.Equal(["a", "b", "c", "d", "n"], items.Keys.Order());
        Assert.Equal("a", items["b"].ParentId);
        Assert.Equal("b", items["c"].ParentId);
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public async Task Undoing_it_once_it_has_gone_makes_it_again_with_its_open_sub_tasks_under_it()
    {
        // Todoist can't undelete, so each comes back as a new task under the new copy of its
        // parent. The one already finished doesn't come back to do.
        var (engine, api) = WithApi(Family());
        engine.DeleteItem("a");
        await Flush(engine, api);

        Assert.True(engine.Undo());

        var made = engine.Snapshot().Items.Where(i => i.Content is "A" or "B" or "C" or "D").ToDictionary(i => i.Content);
        Assert.Equal(["A", "B", "C"], made.Keys.Order());
        Assert.Null(made["A"].ParentId);
        Assert.Equal(made["A"].Id, made["B"].ParentId);
        Assert.Equal(made["B"].Id, made["C"].ParentId);
        Assert.Equal(3, engine.Outbox.Count(c => c.Type == "item_add"));
    }

    [Fact]
    public async Task A_delete_the_server_refuses_puts_its_sub_tasks_back_as_well()
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();
        engine.DeleteItem("a");

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected")),
        };
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b", "c", "d", "n"], Items(engine).Keys.Order());
    }

    [Fact]
    public void After_a_restart_one_still_queued_is_taken_back_whole()
    {
        var store = Family();
        NewEngine(store).DeleteItem("a");

        var reopened = NewEngine(store);
        Assert.True(reopened.Undo());

        Assert.Equal(["a", "b", "c", "d", "n"], Items(reopened).Keys.Order());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>The tasks the outbox is asking the server to act on with this command, in order.</summary>
    private static List<string> Asked(SyncEngine engine, string type)
        => engine.Outbox
            .Where(c => c.Type == type)
            .Select(c => JsonNode.Parse(c.ArgsJson)!["id"]!.ToString())
            .ToList();

    private static Dictionary<string, TaskItem> Items(SyncEngine engine)
        => engine.Snapshot().Items.ToDictionary(i => i.Id);

    private static SyncEngine NewEngine(InMemorySnapshotStore store) => WithApi(store).Engine;

    private static (SyncEngine Engine, FakeApi Api) WithApi(InMemorySnapshotStore store)
    {
        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
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
