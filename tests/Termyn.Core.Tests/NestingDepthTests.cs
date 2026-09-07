using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// How deep sub-tasks are allowed to go.
/// </summary>
/// <remarks>
/// Todoist takes four levels below a top-level task, which is said in its sub-tasks help article
/// and nowhere near the page listing every other cap. Held here because the server has no clean
/// answer for being sent something deeper — what has been reported of it is a generic sync error,
/// or a 502 — so a change queued past the limit would fail long after the keystroke that made it,
/// with nothing on screen tying the two together.
/// </remarks>
public class NestingDepthTests
{
    /// <summary>A chain of tasks, each the child of the one before it.</summary>
    /// <param name="length">How many tasks, so a length of five reaches the deepest Todoist holds</param>
    private static SyncEngine Chain(int length)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");

        for (var i = 0; i < length; i++)
        {
            var parent = i == 0 ? string.Empty : $""","parent_id":"t{i - 1}" """.TrimEnd();
            store.PutResource("items", $"t{i}", $$"""{"id":"t{{i}}","content":"Task {{i}}","project_id":"p","child_order":1{{parent}}}""");
        }

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    /// <summary>
    /// A chain, with one more task alongside its deepest — the one that would be indented.
    /// </summary>
    /// <remarks>
    /// Alongside the deepest and not after the whole chain, because indenting adopts the sibling
    /// immediately above and nothing else. A task following the chain's root would go under the
    /// root and land at depth one, which is nowhere near the limit and would test nothing.
    /// </remarks>
    /// <param name="length">How many tasks in the chain, so the deepest sits at length minus one</param>
    /// <param name="followerChildren">How many levels the task being indented carries with it</param>
    private static SyncEngine ChainWithSibling(int length, int followerChildren = 0)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");

        for (var i = 0; i < length; i++)
        {
            var parent = i == 0 ? string.Empty : $""","parent_id":"t{i - 1}" """.TrimEnd();
            store.PutResource("items", $"t{i}", $$"""{"id":"t{{i}}","content":"Task {{i}}","project_id":"p","child_order":1{{parent}}}""");
        }

        // The chain's deepest task has a parent one level up; this shares it, so the two are
        // siblings and this one is what an indent would put underneath the other.
        store.PutResource("items", "next", $$"""{"id":"next","content":"Next","project_id":"p","parent_id":"t{{length - 2}}","child_order":2}""");

        for (var i = 0; i < followerChildren; i++)
        {
            var parent = i == 0 ? "next" : $"n{i - 1}";
            store.PutResource("items", $"n{i}", $$"""{"id":"n{{i}}","content":"Under {{i}}","project_id":"p","parent_id":"{{parent}}","child_order":1}""");
        }

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    [Fact]
    public void The_limit_is_the_four_levels_Todoist_documents()
        => Assert.Equal(4, SyncEngine.MaxDepth);

    [Fact]
    public void A_task_can_be_indented_to_the_deepest_level_there_is()
    {
        // The sibling it would go under sits at depth three, so this lands at four — the last one
        // allowed. Refusing here would be refusing something Todoist takes.
        var engine = ChainWithSibling(length: 4);

        Assert.True(engine.CanIndentItem("next"));
        Assert.True(engine.IndentItem("next"));
    }

    [Fact]
    public void A_task_is_not_indented_past_it()
    {
        // The sibling it would go under is already at depth four, so this would land at five.
        var engine = ChainWithSibling(length: 5);

        Assert.False(engine.CanIndentItem("next"));
        Assert.False(engine.IndentItem("next"));
        Assert.True(engine.IndentTooDeep("next"));
    }

    [Fact]
    public void What_is_under_a_task_goes_down_with_it()
    {
        // The part most easily missed: the task itself would land at depth three, inside the limit,
        // but it is carrying two levels of its own and the deepest of those would come to rest at
        // five. Counting the task alone would let it through.
        var engine = ChainWithSibling(length: 3, followerChildren: 2);

        Assert.False(engine.CanIndentItem("next"));
        Assert.True(engine.IndentTooDeep("next"));
    }

    [Fact]
    public void A_task_carrying_nothing_fits_where_one_carrying_something_does_not()
    {
        // The same place, the same depth, and the answer turns only on what is being brought along.
        Assert.True(ChainWithSibling(length: 3).CanIndentItem("next"));
        Assert.False(ChainWithSibling(length: 3, followerChildren: 2).CanIndentItem("next"));
    }

    [Fact]
    public void Nothing_above_it_is_not_the_same_as_too_deep()
    {
        // The first task in a list can't indent for want of somewhere to go, and the refusal says
        // so rather than blaming a depth it is nowhere near.
        var engine = Chain(1);

        Assert.False(engine.CanIndentItem("t0"));
        Assert.False(engine.IndentTooDeep("t0"));
    }

    [Fact]
    public void A_task_that_is_not_there_is_neither()
    {
        var engine = Chain(1);

        Assert.False(engine.CanIndentItem("no such task"));
        Assert.False(engine.IndentTooDeep("no such task"));
    }
}
