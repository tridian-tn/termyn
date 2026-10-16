using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// Putting a dragged task down beside another, or onto one.
/// </summary>
/// <remarks>
/// Every drop here goes through <see cref="Placed"/> or <see cref="Nested"/>, which ask first, the
/// way a drag does on its way over, and check the answer matches what the drop then does. A drag
/// that offered somewhere the drop then refused would look like it was ignored.
/// </remarks>
public class DropPlacementTests
{
    // ---- Beside a sibling ----------------------------------------------------------------------

    [Fact]
    public void A_task_put_down_before_a_sibling_moves_up_to_it()
    {
        var engine = ThreeSiblings();

        Assert.True(Placed(engine, "c", "a", after: false));

        Assert.Equal(["c", "a", "b"], Order(engine));
        Assert.Equal("item_reorder", engine.Outbox.Single().Type);
    }

    [Fact]
    public void A_task_put_down_after_a_sibling_moves_down_to_it()
    {
        var engine = ThreeSiblings();

        Assert.True(Placed(engine, "a", "c", after: true));

        Assert.Equal(["b", "c", "a"], Order(engine));
        Assert.Equal("item_reorder", engine.Outbox.Single().Type);
    }

    [Fact]
    public void A_task_put_down_where_it_already_is_sends_nothing()
    {
        // Just after the one above it, and just before the one below: both are where it is now.
        var engine = ThreeSiblings();

        Assert.False(Placed(engine, "b", "a", after: true));
        Assert.False(Placed(engine, "b", "c", after: false));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_drop_that_only_moves_a_task_past_a_completed_one_sends_nothing()
    {
        // The completed task isn't drawn between the other two, so moving "b" up past it leaves
        // the list looking exactly as it did.
        var store = Projects();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "x", """{"id":"x","content":"Done","project_id":"p","child_order":2,"checked":true}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":3}""");
        var engine = NewEngine(store);

        Assert.False(Placed(engine, "b", "a", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_is_not_put_down_beside_itself()
    {
        var engine = ThreeSiblings();

        Assert.False(Placed(engine, "b", "b", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_completed_task_is_neither_moved_nor_put_down_beside()
    {
        var store = Projects();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        store.PutResource("items", "x", """{"id":"x","content":"Done","project_id":"p","child_order":3,"checked":true}""");
        var engine = NewEngine(store);

        Assert.False(Placed(engine, "x", "a", after: false));
        Assert.False(Placed(engine, "a", "x", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_the_model_lacks_is_nothing_to_put_down_or_beside()
    {
        var engine = ThreeSiblings();

        Assert.False(Placed(engine, "ghost", "a", after: false));
        Assert.False(Placed(engine, "a", "ghost", after: false));
        Assert.Equal(0, engine.PendingCount);
    }

    // ---- Beside a task at another level --------------------------------------------------------

    [Fact]
    public void A_sub_task_put_down_beside_a_top_level_task_comes_out_from_under_its_parent()
    {
        var engine = ParentWithChildAndSibling();

        Assert.True(Placed(engine, "c", "b", after: false));

        Assert.Null(Task(engine, "c").ParentId);
        Assert.Equal(["a", "c", "b"], Order(engine));
        Assert.Equal(["item_move", "item_reorder"], engine.Outbox.Select(c => c.Type));
        Assert.Equal("p", Args(engine.Outbox[0])["project_id"]!.ToString());
    }

    [Fact]
    public void A_top_level_task_put_down_beside_a_sub_task_goes_under_the_same_parent()
    {
        var engine = ParentWithChildAndSibling();

        Assert.True(Placed(engine, "b", "c", after: false));

        Assert.Equal("a", Task(engine, "b").ParentId);
        Assert.Equal(["b", "c"], Order(engine, parentId: "a"));
        Assert.Equal(["item_move", "item_reorder"], engine.Outbox.Select(c => c.Type));
        Assert.Equal("a", Args(engine.Outbox[0])["parent_id"]!.ToString());
    }

    [Fact]
    public void A_task_put_down_beside_one_in_a_section_goes_into_that_section()
    {
        // So it's still in that section's view, which is where it was dropped.
        var store = Projects();
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "x", """{"id":"x","content":"X","project_id":"p","section_id":"s1","child_order":2}""");
        var engine = NewEngine(store);

        Assert.True(Placed(engine, "c", "x", after: true));

        var moved = Task(engine, "c");
        Assert.Null(moved.ParentId);
        Assert.Equal("s1", moved.SectionId);
        Assert.Equal("s1", Args(engine.Outbox[0])["section_id"]!.ToString());
        Assert.Equal(["a", "x", "c"], Order(engine));
    }

    [Fact]
    public void A_task_put_down_beside_one_in_another_project_moves_there_with_its_sub_tasks()
    {
        var store = Projects();
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":1}""");
        store.PutResource("items", "h", """{"id":"h","content":"H","project_id":"q","child_order":1}""");
        store.PutResource("items", "hc", """{"id":"hc","content":"Under H","project_id":"q","parent_id":"h","child_order":1}""");
        var engine = NewEngine(store);

        Assert.True(Placed(engine, "h", "b", after: false));

        Assert.Equal("p", Task(engine, "h").ProjectId);
        Assert.Equal("p", Task(engine, "hc").ProjectId);
        Assert.Equal(["h", "b"], Order(engine));
    }

    [Fact]
    public void A_task_captured_before_the_inbox_was_known_has_no_move_to_make_beside_an_inbox_task()
    {
        // It has no project yet and is in the Inbox all the same, so the move there would be turned
        // away as already being there. The drag mustn't offer a drop the engine then refuses.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "in", """{"id":"in","name":"Inbox","is_inbox_project":true,"child_order":0}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"in","child_order":1}""");
        store.PutResource("items", "n", """{"id":"n","content":"New","child_order":2}""");
        var engine = NewEngine(store);

        Assert.False(Placed(engine, "n", "a", after: false));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_is_not_put_down_beside_one_of_its_own_sub_tasks()
    {
        // That would be under itself, with nowhere left for the tree to hang from.
        var engine = ParentWithChildAndSibling();

        Assert.False(Placed(engine, "a", "c", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_is_not_put_down_where_its_sub_tasks_would_go_too_deep()
    {
        // Beside the deepest of the chain, "m" would sit at four and its child at five.
        var engine = ChainAndATaskWithAChild();

        Assert.False(Placed(engine, "m", "t4", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_can_be_put_down_as_deep_as_its_sub_tasks_still_fit()
    {
        // Beside the one above the deepest, "m" sits at three and its child at four: the limit.
        var engine = ChainAndATaskWithAChild();

        Assert.True(Placed(engine, "m", "t3", after: true));
        Assert.Equal("t2", Task(engine, "m").ParentId);
    }

    // ---- Onto a task ---------------------------------------------------------------------------

    [Fact]
    public void A_task_dropped_onto_another_becomes_its_last_sub_task()
    {
        var engine = ParentWithChildAndSibling();

        Assert.True(Nested(engine, "b", "a"));

        Assert.Equal("a", Task(engine, "b").ParentId);
        Assert.Equal(["c", "b"], Order(engine, parentId: "a"));
        Assert.Equal("a", Args(engine.Outbox.Single())["parent_id"]!.ToString());
    }

    [Fact]
    public void A_task_dropped_onto_its_own_parent_stays_where_it_is()
    {
        // The server would file it last under the same parent, which is a reorder nobody asked for.
        var engine = ParentWithChildAndSibling();

        Assert.False(Nested(engine, "c", "a"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_is_not_dropped_onto_itself_or_anything_under_it()
    {
        var store = Projects();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "g", """{"id":"g","content":"G","project_id":"p","parent_id":"c","child_order":1}""");
        var engine = NewEngine(store);

        // Onto "c" is within the depth limit, so it's being under itself that turns it away.
        Assert.False(Nested(engine, "a", "a"));
        Assert.False(Nested(engine, "a", "c"));
        Assert.False(Nested(engine, "a", "g"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_is_not_dropped_where_it_would_go_too_deep()
    {
        var engine = ChainAndATaskWithAChild();

        Assert.False(Nested(engine, "m", "t3"));
        Assert.True(Nested(engine, "m", "t2"));
    }

    [Fact]
    public void Nothing_is_dropped_onto_a_completed_task_and_a_completed_task_is_not_dropped()
    {
        var store = Projects();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "x", """{"id":"x","content":"Done","project_id":"p","child_order":2,"checked":true}""");
        var engine = NewEngine(store);

        Assert.False(Nested(engine, "a", "x"));
        Assert.False(Nested(engine, "x", "a"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_dropped_onto_one_in_another_project_moves_there()
    {
        var store = Projects();
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":1}""");
        store.PutResource("items", "h", """{"id":"h","content":"H","project_id":"q","child_order":1}""");
        var engine = NewEngine(store);

        Assert.True(Nested(engine, "h", "b"));

        var moved = Task(engine, "h");
        Assert.Equal("b", moved.ParentId);
        Assert.Equal("p", moved.ProjectId);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>Puts a task down beside another, having asked first whether it could be.</summary>
    /// <returns>Whether it was put down</returns>
    private static bool Placed(SyncEngine engine, string id, string besideId, bool after)
    {
        var offered = engine.CanPlaceItem(id, besideId, after);
        var placed = engine.PlaceItem(id, besideId, after);

        Assert.Equal(offered, placed);
        return placed;
    }

    /// <summary>Drops a task onto another, having asked first whether it could be.</summary>
    /// <returns>Whether it was dropped</returns>
    private static bool Nested(SyncEngine engine, string id, string parentId)
    {
        var offered = engine.CanNestItem(id, parentId);
        var nested = engine.NestItem(id, parentId);

        Assert.Equal(offered, nested);
        return nested;
    }

    private static Model.TaskItem Task(SyncEngine engine, string id) => engine.Snapshot().Items.Single(i => i.Id == id);

    /// <summary>The tasks still to do at one level of project "p", in the order they're held.</summary>
    private static string[] Order(SyncEngine engine, string? parentId = null)
        => engine.Snapshot().Items
            .Where(i => i.ProjectId == "p" && i.ParentId == parentId && !i.Completed)
            .OrderBy(i => i.ChildOrder)
            .Select(i => i.Id)
            .ToArray();

    private static SyncEngine ThreeSiblings()
    {
        var store = Projects();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","child_order":3}""");
        return NewEngine(store);
    }

    /// <summary>"a" with "c" under it, and "b" alongside "a".</summary>
    private static SyncEngine ParentWithChildAndSibling()
    {
        var store = Projects();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        return NewEngine(store);
    }

    /// <summary>
    /// A chain from "t0" down to "t4", the deepest Todoist holds, and a top-level "m" with one
    /// sub-task, so it carries a level with it wherever it goes.
    /// </summary>
    private static SyncEngine ChainAndATaskWithAChild()
    {
        var store = Projects();

        for (var i = 0; i < 5; i++)
        {
            var parent = i == 0 ? string.Empty : $""","parent_id":"t{i - 1}" """.TrimEnd();
            store.PutResource("items", $"t{i}", $$"""{"id":"t{{i}}","content":"Task {{i}}","project_id":"p","child_order":1{{parent}}}""");
        }

        store.PutResource("items", "m", """{"id":"m","content":"M","project_id":"p","child_order":2}""");
        store.PutResource("items", "mc", """{"id":"mc","content":"Under M","project_id":"p","parent_id":"m","child_order":1}""");
        return NewEngine(store);
    }

    private static InMemorySnapshotStore Projects()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("projects", "q", """{"id":"q","name":"Home","child_order":2}""");
        return store;
    }

    private static SyncEngine NewEngine(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    private static JsonObject Args(OutboxCommand cmd) => (JsonObject)JsonNode.Parse(cmd.ArgsJson)!;
}
