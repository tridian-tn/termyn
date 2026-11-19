using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// Several tasks ticked off or deleted at once, and the one Ctrl+Z that takes them back.
/// </summary>
/// <remarks>
/// They went as one thing done, so they come back as one. Pressing Ctrl+Z once per task to undo a
/// single press of Delete would leave the user counting, and stopping one short leaves a task gone
/// that they meant to keep.
/// </remarks>
public class BatchUndoTests
{
    // ---- Taken back in one ---------------------------------------------------------------------

    [Fact]
    public void Tasks_ticked_off_together_come_back_with_one_undo()
    {
        var engine = NewEngine(Tasks("a", "b", "c"));

        engine.CompleteItems(["a", "b", "c"]);

        Assert.True(engine.Undo());

        Assert.All(["a", "b", "c"], id => Assert.False(Items(engine)[id].Completed));
        Assert.Empty(engine.Outbox);
        Assert.False(engine.CanUndo);
    }

    [Fact]
    public void Tasks_deleted_together_come_back_with_one_undo()
    {
        var engine = NewEngine(Tasks("a", "b", "c"));

        engine.DeleteItems(["a", "b", "c"]);
        Assert.Empty(engine.Snapshot().Items);

        Assert.True(engine.Undo());

        Assert.Equal(["a", "b", "c"], Items(engine).Keys.Order());
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public async Task Tasks_ticked_off_together_and_sent_are_reopened_together()
    {
        var (engine, api) = WithApi(Tasks("a", "b"));
        engine.CompleteItems(["a", "b"]);
        await Flush(engine, api);

        Assert.True(engine.Undo());

        Assert.Equal(["a", "b"], Asked(engine, "item_uncomplete").Order());
    }

    [Fact]
    public async Task Tasks_deleted_together_and_sent_are_made_again_together()
    {
        // Todoist can't undelete, so each comes back as a new task with what it held.
        var (engine, api) = WithApi(Tasks("a", "b"));
        engine.DeleteItems(["a", "b"]);
        await Flush(engine, api);

        Assert.True(engine.Undo());

        Assert.Equal(["Task a", "Task b"], engine.Snapshot().Items.Select(i => i.Content).Order());
        Assert.Equal(2, engine.Outbox.Count(c => c.Type == "item_add"));
    }

    // ---- Kept apart from what came before and after --------------------------------------------

    [Fact]
    public void A_task_ticked_off_alone_afterwards_is_taken_back_first_and_alone()
    {
        var engine = NewEngine(Tasks("a", "b", "c"));
        engine.CompleteItems(["a", "b"]);
        engine.CompleteItem("c");

        Assert.True(engine.Undo());

        var items = Items(engine);
        Assert.False(items["c"].Completed);
        Assert.True(items["a"].Completed);
        Assert.True(items["b"].Completed);

        Assert.True(engine.Undo());

        Assert.False(Items(engine)["a"].Completed);
        Assert.False(Items(engine)["b"].Completed);
    }

    [Fact]
    public void A_task_ticked_off_alone_beforehand_isnt_taken_back_with_the_batch()
    {
        var engine = NewEngine(Tasks("a", "b", "c"));
        engine.CompleteItem("a");
        engine.CompleteItems(["b", "c"]);

        Assert.True(engine.Undo());

        Assert.True(Items(engine)["a"].Completed);
    }

    [Fact]
    public void Two_batches_one_after_the_other_are_two_undos()
    {
        var engine = NewEngine(Tasks("a", "b", "c", "d"));
        engine.CompleteItems(["a", "b"]);
        engine.CompleteItems(["c", "d"]);

        Assert.True(engine.Undo());

        var items = Items(engine);
        Assert.True(items["a"].Completed && items["b"].Completed);
        Assert.False(items["c"].Completed || items["d"].Completed);
    }

    // ---- How deep the stack goes ---------------------------------------------------------------

    [Fact]
    public void A_batch_bigger_than_the_undo_limit_comes_back_whole()
    {
        // The stack holds fifty undos. Counted per task, a batch of sixty kept only its last fifty,
        // and ten tasks stayed ticked off after the Ctrl+Z that should have brought them back.
        var ids = Enumerable.Range(1, 60).Select(n => $"t{n}").ToArray();
        var engine = NewEngine(Tasks(ids));

        engine.CompleteItems(ids);

        Assert.True(engine.Undo());
        Assert.All(ids, id => Assert.False(Items(engine)[id].Completed));
    }

    [Fact]
    public void The_undo_limit_counts_batches_not_tasks()
    {
        // A batch of three and forty-nine more is fifty undos, which the stack holds. Counted per
        // task it's fifty-two, and the two oldest — two of the batch — were dropped.
        var batch = new[] { "b1", "b2", "b3" };
        var singles = Enumerable.Range(1, 49).Select(n => $"s{n}").ToArray();
        var engine = NewEngine(Tasks([.. batch, .. singles]));

        engine.CompleteItems(batch);
        foreach (var id in singles)
        {
            engine.CompleteItem(id);
        }

        for (var i = 0; i < singles.Length; i++)
        {
            Assert.True(engine.Undo());
        }

        Assert.True(engine.Undo());
        Assert.All(batch, id => Assert.False(Items(engine)[id].Completed));
    }

    [Fact]
    public void The_oldest_batch_goes_whole_when_the_stack_is_full()
    {
        // Past the limit the oldest undo is dropped, and a batch is one undo: it goes all at once
        // rather than leaving part of it to be brought back by itself.
        var batch = new[] { "b1", "b2" };
        var singles = Enumerable.Range(1, 50).Select(n => $"s{n}").ToArray();
        var engine = NewEngine(Tasks([.. batch, .. singles]));

        engine.CompleteItems(batch);
        foreach (var id in singles)
        {
            engine.CompleteItem(id);
        }

        for (var i = 0; i < singles.Length; i++)
        {
            Assert.True(engine.Undo());
        }

        Assert.False(engine.Undo());
        Assert.All(batch, id => Assert.True(Items(engine)[id].Completed));
    }

    // ---- What can't be taken back --------------------------------------------------------------

    [Fact]
    public async Task A_repeating_task_sent_with_the_batch_stays_moved_on_while_the_rest_come_back()
    {
        // The server has already moved it to its next date, and there's no putting that back. The
        // others still can be, and the undo was asked for them as much as for it.
        var store = Tasks("a");
        store.PutResource("items", "r", """{"id":"r","content":"Water the plants","project_id":"p","child_order":9,"due":{"date":"2026-07-31","string":"every day","is_recurring":true}}""");
        var (engine, api) = WithApi(store);

        engine.CompleteItems(["r", "a"]);
        await Flush(engine, api);

        Assert.True(engine.Undo());

        Assert.Equal(["a"], Asked(engine, "item_uncomplete"));
    }

    // ---- Reopening several ---------------------------------------------------------------------

    [Fact]
    public async Task Reopening_a_parent_and_its_sub_task_together_reopens_the_sub_task_once()
    {
        // Putting the parent back puts back what its close ticked off with it, so the sub-task is
        // open again by the time its own turn comes. Reopened again, it was asked for twice.
        var store = Tasks("x");
        store.PutResource("items", "p1", """{"id":"p1","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "c1", """{"id":"c1","content":"Child","project_id":"p","parent_id":"p1","child_order":1}""");
        var (engine, api) = WithApi(store);

        engine.CompleteItem("p1");
        await Flush(engine, api);

        engine.ReopenItems(["p1", "c1"]);

        Assert.Equal(["c1", "p1"], Asked(engine, "item_uncomplete").Order());
    }

    [Fact]
    public void Reopening_a_sub_task_and_its_parent_takes_back_the_parents_close_whichever_comes_first()
    {
        // Completed rows are listed by when they were finished, and a parent and what its close took
        // share a moment, so the sub-task can come first. Reopened first, it opened the parent the
        // way the server does, the parent was passed over, and its close was still sent.
        var store = Tasks("x");
        store.PutResource("items", "p1", """{"id":"p1","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "c1", """{"id":"c1","content":"Child","project_id":"p","parent_id":"p1","child_order":1}""");
        store.PutResource("items", "c2", """{"id":"c2","content":"Other child","project_id":"p","parent_id":"p1","child_order":2}""");
        var engine = NewEngine(store);
        engine.CompleteItem("p1");

        engine.ReopenItems(["c1", "p1"]);

        Assert.Empty(engine.Outbox);
        Assert.All(["p1", "c1", "c2"], id => Assert.False(Items(engine)[id].Completed));
        Assert.False(engine.CanUndo);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>The tasks the outbox names in commands of one kind, in the order it'll send them.</summary>
    private static List<string> Asked(SyncEngine engine, string type)
        => engine.Outbox
            .Where(c => c.Type == type)
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

    private static SyncEngine NewEngine(InMemorySnapshotStore store) => WithApi(store).Engine;

    private static (SyncEngine Engine, FakeApi Api) WithApi(InMemorySnapshotStore store)
    {
        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(new DateOnly(2026, 7, 31)));
        engine.Load();
        return (engine, api);
    }

    /// <summary>One project holding a top-level task for each id, called "Task" and the id.</summary>
    private static InMemorySnapshotStore Tasks(params string[] ids)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");

        for (var i = 0; i < ids.Length; i++)
        {
            store.PutResource("items", ids[i], new JsonObject
            {
                ["id"] = ids[i],
                ["content"] = $"Task {ids[i]}",
                ["project_id"] = "p",
                ["child_order"] = i + 1,
            }.ToJsonString());
        }

        return store;
    }
}
