using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// Moving a project or a section among the rows it sits with.
/// </summary>
/// <remarks>
/// The same machinery a task's move goes through, told which of the three commands it is. Todoist
/// gives each its own name and its own words for the array and the position, and getting one of
/// those wrong is a command that fails forever in the outbox rather than anything that shows up
/// here — so the wire shape is asserted verbatim.
/// </remarks>
public class StructureReorderTests
{
    // ---- The wire ----------------------------------------------------------------------------------

    [Fact]
    public void Moving_a_project_sends_the_command_Todoist_names()
    {
        var engine = Projects(("a", 1), ("b", 2));

        Assert.True(engine.MoveProject("b", -1));

        var cmd = engine.Outbox.Single();
        Assert.Equal("project_reorder", cmd.Type);

        var entries = Args(cmd)["projects"]!.AsArray();
        Assert.Equal(["b", "a"], entries.Select(e => e!["id"]!.ToString()));
        Assert.Equal(["1", "2"], entries.Select(e => e!["child_order"]!.ToString()));
    }

    [Fact]
    public void Moving_a_section_sends_the_command_Todoist_names()
    {
        // A section counts its place in a field of its own — section_order, not child_order — so
        // the two commands are not one command with a different name on it.
        var engine = Sections(("s1", 1), ("s2", 2));

        Assert.True(engine.MoveSection("s2", -1));

        var cmd = engine.Outbox.Single();
        Assert.Equal("section_reorder", cmd.Type);

        var entries = Args(cmd)["sections"]!.AsArray();
        Assert.Equal(["s2", "s1"], entries.Select(e => e!["id"]!.ToString()));
        Assert.Equal(["1", "2"], entries.Select(e => e!["section_order"]!.ToString()));
        Assert.All(entries, e => Assert.Null(e!["child_order"]));
    }

    [Fact]
    public void Only_the_rows_that_actually_moved_are_sent()
    {
        // A one-place move among six otherwise rewrites and re-persists all six.
        var engine = Projects(("a", 1), ("b", 2), ("c", 3), ("d", 4), ("e", 5), ("f", 6));

        engine.MoveProject("e", -1);

        var entries = Args(engine.Outbox.Single())["projects"]!.AsArray();
        Assert.Equal(["e", "d"], entries.Select(e => e!["id"]!.ToString()));
    }

    // ---- Which rows count as neighbours ------------------------------------------------------------

    [Fact]
    public void A_project_moves_among_the_ones_sharing_its_parent()
    {
        // Sub-projects are a list of their own. Counting them here would swap a project with one
        // sitting at a different depth, which on screen is not the row above it.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "top1", """{"id":"top1","name":"One","child_order":1}""");
        store.PutResource("projects", "top2", """{"id":"top2","name":"Two","child_order":2}""");
        store.PutResource("projects", "kid", """{"id":"kid","name":"Kid","parent_id":"top1","child_order":1}""");
        var engine = Loaded(store);

        // The only child of its parent, so there is nowhere for it to go either way.
        Assert.False(engine.CanMoveProject("kid", -1));
        Assert.False(engine.CanMoveProject("kid", 1));

        // And the two at the top still see each other.
        Assert.True(engine.CanMoveProject("top1", 1));
    }

    [Fact]
    public void A_section_moves_among_the_ones_in_its_own_project()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "mine", """{"id":"mine","name":"Mine","project_id":"p1","section_order":1}""");
        store.PutResource("sections", "theirs", """{"id":"theirs","name":"Theirs","project_id":"p2","section_order":2}""");
        var engine = Loaded(store);

        Assert.False(engine.CanMoveSection("mine", 1));
    }

    [Fact]
    public void The_Inbox_is_left_out_of_the_reckoning_and_cannot_be_moved()
    {
        // Todoist keeps it at the head of the list whatever its order says. Letting it take part
        // would send positions the server ignores and leave the sidebar disagreeing with the app.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "in", """{"id":"in","name":"Inbox","inbox_project":true,"child_order":1}""");
        store.PutResource("projects", "a", """{"id":"a","name":"A","child_order":2}""");
        store.PutResource("projects", "b", """{"id":"b","name":"B","child_order":3}""");
        var engine = Loaded(store);

        Assert.False(engine.CanMoveProject("in", 1));
        Assert.False(engine.CanMoveProject("in", -1));

        // And "a" is at the top of what can move, the Inbox not counting as the row above it.
        Assert.False(engine.CanMoveProject("a", -1));
        Assert.True(engine.CanMoveProject("a", 1));
    }

    [Fact]
    public void An_archived_project_is_not_a_neighbour()
    {
        // It isn't in the sidebar, so swapping with one would look like the keypress did nothing.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "a", """{"id":"a","name":"A","child_order":1}""");
        store.PutResource("projects", "gone", """{"id":"gone","name":"Gone","is_archived":true,"child_order":2}""");
        store.PutResource("projects", "b", """{"id":"b","name":"B","child_order":3}""");
        var engine = Loaded(store);

        engine.MoveProject("b", -1);

        var entries = Args(engine.Outbox.Single())["projects"]!.AsArray();
        Assert.Equal(["b", "a"], entries.Select(e => e!["id"]!.ToString()));
    }

    // ---- The ends ----------------------------------------------------------------------------------

    [Fact]
    public void Nothing_moves_off_either_end()
    {
        var engine = Projects(("a", 1), ("b", 2));

        Assert.False(engine.MoveProject("a", -1));
        Assert.False(engine.MoveProject("b", 1));
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public void A_row_that_is_not_held_does_not_move()
    {
        var engine = Projects(("a", 1), ("b", 2));

        Assert.False(engine.MoveProject("ghost", 1));
        Assert.False(engine.MoveSection("ghost", 1));
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public void The_question_and_the_act_agree()
    {
        // Asked by the menu and by the keystroke separately, so an answer that differed from what
        // happens would grey an entry that works or offer one that doesn't.
        var engine = Projects(("a", 1), ("b", 2), ("c", 3));

        foreach (var (id, offset) in new[] { ("a", -1), ("a", 1), ("b", -1), ("b", 1), ("c", -1), ("c", 1) })
        {
            var could = engine.CanMoveProject(id, offset);
            Assert.Equal(could, engine.MoveProject(id, offset));
        }
    }

    // ---- What the move does locally ----------------------------------------------------------------

    [Fact]
    public void The_new_order_is_visible_before_the_server_hears_about_it()
    {
        var engine = Projects(("a", 1), ("b", 2), ("c", 3));

        engine.MoveProject("c", -1);

        Assert.Equal(["a", "c", "b"], Ordered(engine));
    }

    [Fact]
    public void Moving_twice_carries_on_from_where_the_first_left_it()
    {
        var engine = Projects(("a", 1), ("b", 2), ("c", 3));

        engine.MoveProject("c", -1);
        engine.MoveProject("c", -1);

        Assert.Equal(["c", "a", "b"], Ordered(engine));
    }

    // ---- The outbox --------------------------------------------------------------------------------

    [Fact]
    public async Task A_pending_reorder_keeps_its_positions_when_a_sync_lands()
    {
        // The position is ours until the command lands; everything else the server sends about the
        // same project is taken as it comes.
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "a", """{"id":"a","name":"A","child_order":1}""");
        store.PutResource("projects", "b", """{"id":"b","name":"B","child_order":2}""");
        var engine = Loaded(store, api);

        engine.MoveProject("b", -1);

        api.Next = _ => Resp("s1", [Json.Change("projects", "b", """{"id":"b","name":"Renamed","child_order":2}""")]);
        await engine.SyncAsync();

        var moved = engine.Snapshot().Projects.Single(p => p.Id == "b");
        Assert.Equal("Renamed", moved.Name);
        Assert.Equal(1, moved.ChildOrder);
    }

    [Fact]
    public async Task A_cancelled_reorder_puts_the_other_rows_back()
    {
        // The reorder goes with the failed create that it named, so the server never hears about
        // it — and the rows it renumbered must not keep a position nothing will ever be told.
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "a", """{"id":"a","name":"A","child_order":1}""");
        store.PutResource("projects", "b", """{"id":"b","name":"B","child_order":2}""");
        var engine = Loaded(store, api);

        // Down, not up. A project the server hasn't named yet carries no order at all, which reads
        // as nought and sorts it above everything — so moving it up is a move it hasn't got, and
        // this test queued nothing and proved nothing until it was asked which way it had gone.
        var temp = engine.AddProject("New");
        Assert.True(engine.MoveProject(temp, 1));
        Assert.Contains(engine.Outbox, c => c.Type == "project_reorder");

        api.Next = cmds =>
        {
            var add = cmds.First(c => c.Type == "project_add");
            return Resp("s1", status: (add.Uuid, false));
        };
        await engine.SyncAsync();

        var order = engine.Snapshot().Projects.ToDictionary(p => p.Id, p => p.ChildOrder);
        Assert.Equal(1, order["a"]);
        Assert.Equal(2, order["b"]);
        Assert.DoesNotContain(engine.Outbox, c => c.Type == "project_reorder");
    }

    [Fact]
    public async Task A_pending_section_reorder_keeps_the_order_field_a_section_uses()
    {
        // The same guard as the project one above, and the reason it is worth writing twice: a
        // section keeps its place in section_order, and a guard that knew only child_order would
        // protect a field the section hasn't got while losing the one it has.
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "s1", """{"id":"s1","name":"One","project_id":"p","section_order":1}""");
        store.PutResource("sections", "s2", """{"id":"s2","name":"Two","project_id":"p","section_order":2}""");
        var engine = Loaded(store, api);

        engine.MoveSection("s2", -1);

        api.Next = _ => Resp(
            "s1",
            [Json.Change("sections", "s2", """{"id":"s2","name":"Renamed","project_id":"p","section_order":2}""")]);
        await engine.SyncAsync();

        var moved = engine.Snapshot().Sections.Single(x => x.Id == "s2");
        Assert.Equal("Renamed", moved.Name);
        Assert.Equal(1, moved.SectionOrder);
    }

    [Fact]
    public async Task A_temp_id_inside_a_queued_project_reorder_is_remapped()
    {
        // A project can be added and then moved before the server has named it, the same as a task.
        // Left as a temp id, the reorder names something the server has never heard of and fails
        // for good — after the add it depended on had already succeeded.
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "a", """{"id":"a","name":"A","child_order":1}""");
        var engine = Loaded(store, api);

        var temp = engine.AddProject("New");
        Assert.True(engine.MoveProject(temp, 1));

        api.Next = cmds =>
        {
            var add = cmds.First(c => c.Type == "project_add");
            return Resp("s1", status: (add.Uuid, true), temp: (temp, "real1"));
        };
        await engine.SyncAsync();

        var reorder = engine.Outbox.Single(c => c.Type == "project_reorder");
        var ids = Args(reorder)["projects"]!.AsArray().Select(e => e!["id"]!.ToString()).ToList();

        Assert.Contains("real1", ids);
        Assert.DoesNotContain(temp, ids);
    }

    [Fact]
    public void Sections_with_no_order_yet_move_the_way_the_sidebar_shows_them()
    {
        // Two sections the server hasn't ordered both read as nought, so what settles them is the
        // tie-break — and the sidebar breaks it by name. By id instead, the row that appeared to
        // swap would be whichever of the two lists disagreed about.
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "zzz", """{"id":"zzz","name":"Alpha","project_id":"p"}""");
        store.PutResource("sections", "aaa", """{"id":"aaa","name":"Beta","project_id":"p"}""");
        var engine = Loaded(store);

        // "Alpha" is shown first though its id sorts last, so it is the one with nowhere above it.
        Assert.False(engine.CanMoveSection("zzz", -1));
        Assert.True(engine.CanMoveSection("aaa", -1));
    }

    // ---- Fixtures ----------------------------------------------------------------------------------

    private static SyncEngine Projects(params (string Id, int Order)[] projects)
    {
        var store = new InMemorySnapshotStore();
        foreach (var (id, order) in projects)
            store.PutResource("projects", id, $$"""{"id":"{{id}}","name":"{{id}}","child_order":{{order}}}""");

        return Loaded(store);
    }

    private static SyncEngine Sections(params (string Id, int Order)[] sections)
    {
        var store = new InMemorySnapshotStore();
        foreach (var (id, order) in sections)
            store.PutResource("sections", id, $$"""{"id":"{{id}}","name":"{{id}}","project_id":"p","section_order":{{order}}}""");

        return Loaded(store);
    }

    private static SyncEngine Loaded(InMemorySnapshotStore store, FakeApi? api = null)
    {
        var engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    /// <summary>The projects that can move, in the order the sidebar would show them.</summary>
    private static string[] Ordered(SyncEngine engine)
        => engine.Snapshot().Projects
            .Where(p => !p.IsInboxProject)
            .OrderBy(p => p.ChildOrder)
            .Select(p => p.Id)
            .ToArray();

    private static JsonObject Args(OutboxCommand cmd) => (JsonObject)JsonNode.Parse(cmd.ArgsJson)!;

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
