using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// The questions a menu asks before it greys an entry. What matters here is not each answer on its
/// own but that it matches what actually happens: an entry offered and then refused, or refused
/// when it would have worked, is worse than no menu at all.
/// </summary>
public class SyncEngineAbilityTests
{
    // ---- The answer matches the act ------------------------------------------------------------

    [Theory]
    [InlineData("a", -1)]
    [InlineData("a", 1)]
    [InlineData("b", -1)]
    [InlineData("b", 1)]
    [InlineData("c", -1)]
    [InlineData("c", 1)]
    [InlineData("unknown", 1)]
    public void Whether_it_can_move_is_what_moving_it_does(string id, int offset)
    {
        // Asked of one engine and done to another, so the act can't have changed the answer.
        var asked = Three().CanMoveItem(id, offset);
        var did = Three().MoveItem(id, offset);

        Assert.Equal(did, asked);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("c")]
    [InlineData("unknown")]
    public void Whether_it_can_indent_is_what_indenting_it_does(string id)
        => Assert.Equal(Three().IndentItem(id), Three().CanIndentItem(id));

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("unknown")]
    public void Whether_it_can_outdent_is_what_outdenting_it_does(string id)
    {
        // A fixture apiece, because outdenting rewrites the one it is given.
        Assert.Equal(ParentAndChild().OutdentItem(id), ParentAndChild().CanOutdentItem(id));
    }

    // ---- And answering doesn't change anything -------------------------------------------------

    [Fact]
    public void Asking_writes_nothing()
    {
        // A menu opening is not an edit. These run on every right-click, and one that queued a
        // command would sync the account each time a user looked at what they could do.
        var engine = Three();

        engine.CanMoveItem("b", 1);
        engine.CanIndentItem("b");
        engine.CanOutdentItem("b");

        Assert.Equal(0, engine.PendingCount);
        Assert.Equal([1, 2, 3], engine.Snapshot().Items.OrderBy(i => i.Id).Select(i => i.ChildOrder).ToArray());
    }

    // ---- The ends of the list ------------------------------------------------------------------

    [Fact]
    public void The_first_task_cannot_move_up_and_the_last_cannot_move_down()
    {
        var engine = Three();

        Assert.False(engine.CanMoveItem("a", -1));
        Assert.True(engine.CanMoveItem("a", 1));
        Assert.True(engine.CanMoveItem("c", -1));
        Assert.False(engine.CanMoveItem("c", 1));
    }

    [Fact]
    public void A_completed_neighbour_is_not_somewhere_to_move_to()
    {
        // Moving onto a row that isn't on screen would look like the key did nothing, so the move
        // steps over it — and with nothing beyond, there is nowhere to go.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2,"checked":true}""");

        Assert.False(Engine(store).CanMoveItem("a", 1));
    }

    [Fact]
    public void A_task_with_only_completed_siblings_above_it_cannot_indent()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1,"checked":true}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");

        Assert.False(Engine(store).CanIndentItem("b"));
    }

    [Fact]
    public void A_top_level_task_has_nothing_to_outdent_to()
        => Assert.False(Three().CanOutdentItem("a"));

    private static SyncEngine Engine(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    /// <summary>A task with one sub-task under it.</summary>
    private static SyncEngine ParentAndChild()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","parent_id":"a","child_order":1}""");
        return Engine(store);
    }

    /// <summary>Three siblings in order, so both ends and the middle can be asked about.</summary>
    private static SyncEngine Three()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","child_order":3}""");
        return Engine(store);
    }
}
