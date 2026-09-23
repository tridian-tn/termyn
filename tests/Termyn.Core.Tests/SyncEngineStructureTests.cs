using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>Hierarchy and project/section operations.</summary>
public class SyncEngineStructureTests
{
    // ---- Indent / outdent ----------------------------------------------------------------------

    [Fact]
    public void Indenting_adopts_the_sibling_above()
    {
        var engine = TwoSiblings();

        Assert.True(engine.IndentItem("b"));

        Assert.Equal("a", engine.Snapshot().Items.Single(i => i.Id == "b").ParentId);
        var cmd = engine.Outbox.Single();
        Assert.Equal("item_move", cmd.Type);
        Assert.Equal("a", Args(cmd)["parent_id"]!.ToString());
    }

    [Fact]
    public void The_first_task_has_nothing_to_indent_under()
    {
        var engine = TwoSiblings();

        Assert.False(engine.IndentItem("a"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Outdenting_promotes_a_subtask_alongside_its_parent()
    {
        var engine = ParentAndChild();

        Assert.True(engine.OutdentItem("c"));

        Assert.Null(engine.Snapshot().Items.Single(i => i.Id == "c").ParentId);
        var cmd = engine.Outbox.Single();
        Assert.Equal("item_move", cmd.Type);
        Assert.Equal("p", Args(cmd)["project_id"]!.ToString());
    }

    [Fact]
    public void Outdenting_a_grandchild_moves_it_under_the_grandparent()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","parent_id":"b","child_order":1}""");
        var engine = NewEngine(store);

        Assert.True(engine.OutdentItem("c"));

        Assert.Equal("a", engine.Snapshot().Items.Single(i => i.Id == "c").ParentId);
    }

    [Fact]
    public void A_top_level_task_cannot_be_outdented()
    {
        var engine = TwoSiblings();

        Assert.False(engine.OutdentItem("a"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Indenting_an_unknown_task_does_nothing()
    {
        var engine = TwoSiblings();

        Assert.False(engine.IndentItem("ghost"));
        Assert.False(engine.OutdentItem("ghost"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Indenting_skips_a_completed_sibling_it_could_not_show_under()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "done", """{"id":"done","content":"Done","project_id":"p","checked":true,"child_order":2}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":3}""");
        var engine = NewEngine(store);

        Assert.True(engine.IndentItem("b"));

        // Adopting the completed task would queue a move that changed nothing on screen.
        Assert.Equal("a", engine.Snapshot().Items.Single(i => i.Id == "b").ParentId);
    }

    [Fact]
    public void A_task_with_only_completed_siblings_above_it_cannot_indent()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "done", """{"id":"done","content":"Done","project_id":"p","checked":true,"child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        var engine = NewEngine(store);

        Assert.False(engine.IndentItem("b"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void An_indented_task_lands_last_among_its_new_siblings()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "c1", """{"id":"c1","content":"C1","project_id":"p","parent_id":"a","child_order":1}""");
        store.PutResource("items", "c2", """{"id":"c2","content":"C2","project_id":"p","parent_id":"a","child_order":2}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        var engine = NewEngine(store);

        engine.IndentItem("b");

        // The server files it last under the new parent; the local copy must not claim otherwise.
        Assert.Equal(3, engine.Snapshot().Items.Single(i => i.Id == "b").ChildOrder);
    }

    [Fact]
    public void A_subtask_takes_its_parents_project_and_section()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","section_id":"s2","child_order":2}""");
        var engine = NewEngine(store);

        engine.IndentItem("b");

        var moved = engine.Snapshot().Items.Single(i => i.Id == "b");
        Assert.Equal("s1", moved.SectionId);
        Assert.Equal("p", moved.ProjectId);
    }

    [Fact]
    public void Outdenting_inside_a_section_keeps_the_task_in_that_section()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","section_id":"s1","parent_id":"a","child_order":1}""");
        var engine = NewEngine(store);

        Assert.True(engine.OutdentItem("c"));

        // Moving to the project would have evicted it from the section server-side.
        var cmd = engine.Outbox.Single();
        Assert.Equal("s1", Args(cmd)["section_id"]!.ToString());

        var moved = engine.Snapshot().Items.Single(i => i.Id == "c");
        Assert.Equal("s1", moved.SectionId);
        Assert.Equal("p", moved.ProjectId); // the project is read off the section, and must survive
    }

    [Fact]
    public void Outdenting_into_a_section_the_model_lacks_falls_back_to_the_project()
    {
        // The parent claims a section that isn't here — deleted upstream, or simply not synced yet.
        // Reading the destination project off it would file the task under no project at all.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"gone","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","section_id":"gone","parent_id":"a","child_order":1}""");
        var engine = NewEngine(store);

        Assert.True(engine.OutdentItem("c"));

        var moved = engine.Snapshot().Items.Single(i => i.Id == "c");
        Assert.Equal("p", moved.ProjectId);
        Assert.Null(moved.ParentId);
        Assert.Equal("p", Args(engine.Outbox.Single())["project_id"]!.ToString());
    }

    [Fact]
    public void Moving_a_task_to_another_project_clears_its_old_section()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "other", """{"id":"other","name":"Other"}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","section_id":"s1","child_order":1}""");
        var engine = NewEngine(store);

        engine.MoveItemToProject("c", "other");

        var moved = engine.Snapshot().Items.Single();
        Assert.Equal("other", moved.ProjectId);
        Assert.Null(moved.SectionId); // the server drops it, so the local copy must too
    }

    [Fact]
    public void Moving_a_task_to_another_project_takes_it_to_the_top_level_there()
    {
        var engine = ParentAndChild();

        Assert.True(engine.MoveItemToProject("c", "other"));

        var moved = engine.Snapshot().Items.Single(i => i.Id == "c");
        Assert.Equal("other", moved.ProjectId);
        Assert.Null(moved.ParentId);
    }

    // ---- Moving elsewhere ----------------------------------------------------------------------

    [Fact]
    public void Moving_a_task_into_a_section_files_it_there_and_in_that_sections_project()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "s9", """{"id":"s9","name":"Admin","project_id":"q"}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","child_order":1}""");
        var engine = NewEngine(store);

        Assert.True(engine.MoveItemToSection("c", "s9"));

        var moved = engine.Snapshot().Items.Single();
        Assert.Equal("s9", moved.SectionId);
        Assert.Equal("q", moved.ProjectId);

        var cmd = engine.Outbox.Single();
        Assert.Equal("item_move", cmd.Type);
        Assert.Equal("s9", Args(cmd)["section_id"]!.ToString());
    }

    [Fact]
    public void A_sub_task_moved_into_a_section_comes_out_from_under_its_parent()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","section_id":"s1","parent_id":"a","child_order":1}""");
        var engine = NewEngine(store);

        // The section it's already in, which is still a move: it's top level there afterwards.
        Assert.True(engine.MoveItemToSection("c", "s1"));

        Assert.Null(engine.Snapshot().Items.Single(i => i.Id == "c").ParentId);
    }

    [Fact]
    public void A_sub_task_moved_to_its_own_project_comes_out_from_under_its_parent()
    {
        var engine = ParentAndChild();

        Assert.True(engine.MoveItemToProject("c", "p"));

        Assert.Null(engine.Snapshot().Items.Single(i => i.Id == "c").ParentId);
    }

    [Fact]
    public void A_section_the_model_lacks_is_nowhere_to_move_to()
    {
        var engine = TwoSiblings();

        Assert.False(engine.MoveItemToSection("a", "gone"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_project_the_model_lacks_is_nowhere_to_move_to()
    {
        // Asked under the same lock as the move. Checked beforehand instead, a sync could take the
        // project away in between and the task would be filed under something no view shows.
        var engine = TwoSiblings();

        Assert.False(engine.MoveItemToProject("a", "gone"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void An_inbox_task_with_no_project_yet_is_not_sent_to_the_inbox_again()
    {
        // A task captured without naming a project has none until the server gives it the Inbox's,
        // and it's in the Inbox all the same.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "inbox", """{"id":"inbox","name":"Inbox","is_inbox_project":true}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","child_order":1}""");
        var engine = NewEngine(store);

        Assert.False(engine.MoveItemToProject("a", "inbox"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_already_top_level_in_a_project_is_not_sent_there_again()
    {
        // The server would take it and file the task last, which is a reorder nobody asked for.
        var engine = TwoSiblings();

        Assert.False(engine.MoveItemToProject("a", "p"));

        Assert.Equal(0, engine.PendingCount);
        Assert.Equal(1, engine.Snapshot().Items.Single(i => i.Id == "a").ChildOrder);
    }

    [Fact]
    public void A_task_already_top_level_in_a_section_is_not_sent_there_again()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        var engine = NewEngine(store);

        Assert.False(engine.MoveItemToSection("a", "s1"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_task_in_a_section_moved_to_its_own_project_leaves_the_section()
    {
        // In the project already, but not top level in it: the section is somewhere else to be.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        var engine = NewEngine(store);

        Assert.True(engine.MoveItemToProject("a", "p"));
        Assert.Null(engine.Snapshot().Items.Single().SectionId);
    }

    [Fact]
    public void A_moved_task_takes_its_sub_tasks_with_it()
    {
        // The server moves them itself and doesn't say so until the next sync. Left behind here they
        // would show in the old project as tasks with no parent, under a parent shown with no children.
        var engine = NewEngine(Family());

        Assert.True(engine.MoveItemToProject("a", "q"));

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.All(["a", "b", "c"], id => Assert.Equal("q", items[id].ProjectId));
        Assert.All(["a", "b", "c"], id => Assert.Null(items[id].SectionId));

        // Still filed under each other, which a move of the top one doesn't change.
        Assert.Equal("a", items["b"].ParentId);
        Assert.Equal("b", items["c"].ParentId);

        // And a task that was only ever a neighbour stays where it was.
        Assert.Equal("p", items["n"].ProjectId);
    }

    [Fact]
    public void A_task_moved_into_a_section_takes_its_sub_tasks_into_it()
    {
        var store = Family();
        store.PutResource("sections", "s9", """{"id":"s9","name":"Admin","project_id":"q"}""");
        var engine = NewEngine(store);

        Assert.True(engine.MoveItemToSection("a", "s9"));

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.All(["b", "c"], id => Assert.Equal("s9", items[id].SectionId));
        Assert.All(["b", "c"], id => Assert.Equal("q", items[id].ProjectId));
    }

    [Fact]
    public void Indenting_under_a_task_in_another_section_takes_the_sub_tasks_into_it()
    {
        // The rarer indent, where the sibling above sits in a different section of the same project.
        // The indented task takes its new parent's section, and what's under it has to follow.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","section_id":"s2","child_order":2}""");
        store.PutResource("items", "b1", """{"id":"b1","content":"B1","project_id":"p","section_id":"s2","parent_id":"b","child_order":1}""");
        var engine = NewEngine(store);

        Assert.True(engine.IndentItem("b"));

        Assert.Equal("s1", engine.Snapshot().Items.Single(i => i.Id == "b1").SectionId);
    }

    [Fact]
    public void Reverting_a_move_puts_its_sub_tasks_back_as_well()
    {
        var engine = NewEngine(Family());
        engine.MoveItemToProject("a", "q");

        engine.Revert(engine.Outbox.Single().Uuid);

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.All(["a", "b", "c"], id => Assert.Equal("p", items[id].ProjectId));
        Assert.All(["a", "b", "c"], id => Assert.Equal("s1", items[id].SectionId));
    }

    [Fact]
    public async Task A_move_the_server_refuses_puts_its_sub_tasks_back_as_well()
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();
        engine.MoveItemToProject("a", "q");

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected")),
        };
        await engine.SyncAsync();

        // The server never moved them, and won't resend what never changed there — so nothing but
        // this would ever bring them home.
        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.All(["a", "b", "c"], id => Assert.Equal("p", items[id].ProjectId));
        Assert.Equal(1, engine.FailedCount);
    }

    [Fact]
    public async Task A_sub_task_carried_by_a_pending_move_isnt_pulled_back_by_the_server()
    {
        // The server hasn't moved it yet, so anything it says about the sub-task meanwhile still has
        // it in the old project. Taken, that leaves it behind under a parent that has gone elsewhere.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 5);
        engine.Load();
        engine.MoveItemToProject("a", "q");

        // No verdict on the move, so it's still pending once this lands.
        api.Next = _ => new SyncResponse
        {
            SyncToken = "s2",
            Changes = [Json.Change("items", "b", """{"id":"b","content":"B","project_id":"p","section_id":"s1","parent_id":"a","child_order":1}""")],
        };
        await engine.SyncAsync();

        Assert.Equal(1, engine.PendingCount);
        Assert.Equal("q", engine.Snapshot().Items.Single(i => i.Id == "b").ProjectId);
    }

    [Fact]
    public async Task A_sub_task_deleted_while_its_move_was_pending_stays_deleted_when_the_move_is_refused()
    {
        // The refusal puts back what the move carried. A tombstone taken before then would be undone
        // by it — and the server doesn't send a tombstone twice, so the sub-task would be back for good.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 2);
        engine.Load();
        engine.MoveItemToProject("a", "q");

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands), Changes = [Json.Deleted("items", "c")] };
        await engine.SyncAsync();

        api.Next = commands => new SyncResponse { SyncToken = "s3", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.DoesNotContain("c", items.Keys);
        Assert.Equal("p", items["b"].ProjectId);
        Assert.Equal(1, engine.FailedCount);
    }

    [Fact]
    public async Task A_refused_move_leaves_one_copy_of_a_sub_task_the_server_has_named_since()
    {
        // Added offline under the task, then carried by its move. The server takes the add and
        // names the sub-task, and refuses the move. Put back under the name it had here, there'd
        // be two of it, for good.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();

        var temp = engine.AddItem(AddedUnder("a"));
        engine.MoveItemToProject("a", "q");
        Assert.Equal("q", engine.Snapshot().Items.Single(i => i.Id == temp).ProjectId);

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = RefusingMoves(commands),
            TempIdMapping = new Dictionary<string, string> { [temp] = "real" },
        };
        await engine.SyncAsync();

        var added = Assert.Single(engine.Snapshot().Items, i => i.Content == "Added offline");
        Assert.Equal("real", added.Id);
        Assert.Equal("p", added.ProjectId);
        Assert.Equal("s1", added.SectionId);
    }

    [Fact]
    public async Task A_refused_move_doesnt_bring_back_a_sub_task_whose_add_was_refused()
    {
        // The refused add takes the sub-task away. The server never had it, so nothing it sends will
        // ever take it away again if the move's refusal puts it back.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();

        engine.AddItem(AddedUnder("a"));
        engine.MoveItemToProject("a", "q");

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        Assert.DoesNotContain(engine.Snapshot().Items, i => i.Content == "Added offline");
        Assert.Equal("p", engine.Snapshot().Items.Single(i => i.Id == "a").ProjectId);
    }

    [Fact]
    public async Task A_task_indented_under_one_whose_add_is_refused_goes_back_where_it_was()
    {
        // The refused add takes the indent with it. The server never moved X, so nothing it sends
        // will put X back, and left alone it sits under a task that isn't there.
        var store = OneTask();
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var added = engine.AddItem(new JsonObject { ["content"] = "Added offline", ["project_id"] = "p", ["child_order"] = 1 });
        Assert.True(engine.IndentItem("x"));
        Assert.Equal(added, engine.Snapshot().Items.Single(i => i.Id == "x").ParentId);

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        var x = engine.Snapshot().Items.Single(i => i.Id == "x");
        Assert.Null(x.ParentId);
        Assert.Equal("p", x.ProjectId);
        Assert.Null(x.SectionId);
        Assert.Equal(2, x.ChildOrder);
        Assert.Equal(0, engine.PendingCount);
        Assert.Equal(0, engine.FailedCount);

        var reloaded = NewEngine(store).Snapshot().Items.Single(i => i.Id == "x");
        Assert.Null(reloaded.ParentId);
        Assert.Equal(2, reloaded.ChildOrder);
    }

    [Fact]
    public async Task A_task_moved_on_after_an_indent_whose_add_is_refused_ends_where_the_server_has_it()
    {
        // Indented under a task added offline, then moved to another project. The move names nothing
        // that was refused, so it goes to the server and lands. Rolling back the indent puts X back
        // in the first project, and the server's copy that comes back with the move says where it
        // really is.
        var store = OneTask();
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.AddItem(new JsonObject { ["content"] = "Added offline", ["project_id"] = "p", ["child_order"] = 1 });
        Assert.True(engine.IndentItem("x"));
        Assert.True(engine.MoveItemToProject("x", "q"));

        // Takes the move to q and refuses the rest.
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(
                c => c.Uuid,
                c => c.Type == "item_move" && c.Args["project_id"] is not null ? new CommandResult(true, null, null) : new CommandResult(false, "ERR", "rejected")),
            Changes = [Json.Change("items", "x", """{"id":"x","content":"X","project_id":"q","parent_id":null,"section_id":null,"child_order":1}""")],
        };
        await engine.SyncAsync();

        var x = engine.Snapshot().Items.Single(i => i.Id == "x");
        Assert.Equal("q", x.ProjectId);
        Assert.Null(x.ParentId);
        Assert.Null(x.SectionId);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task A_later_move_refused_after_an_indent_whose_add_is_refused_puts_the_task_back_where_it_started()
    {
        // The move to another project was queued with X under the task added offline, and that's
        // what its priors said. Refused in its turn, it'd put X back under a task that isn't there.
        // Across a restart, so what it's been told since has to have reached the store.
        var store = OneTask();
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.AddItem(new JsonObject { ["content"] = "Added offline", ["project_id"] = "p", ["child_order"] = 1 });
        Assert.True(engine.IndentItem("x"));

        // Moved while the add and the indent are on the wire, so the move isn't in this round and
        // nothing but the rollback writes its command back to the store.
        api.Next = commands =>
        {
            Assert.True(engine.MoveItemToProject("x", "q"));
            return new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        };
        await engine.SyncAsync();
        Assert.Equal(1, engine.PendingCount);

        var restarted = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        restarted.Load();
        api.Next = commands => new SyncResponse { SyncToken = "s3", SyncStatus = Refused(commands) };
        await restarted.SyncAsync();

        var x = restarted.Snapshot().Items.Single(i => i.Id == "x");
        Assert.Null(x.ParentId);
        Assert.Equal("p", x.ProjectId);
        Assert.Equal(2, x.ChildOrder);
        Assert.Equal(1, restarted.FailedCount);
    }

    [Fact]
    public async Task A_sub_task_ticked_off_with_a_task_whose_add_is_refused_is_put_back_open()
    {
        // Ticking off the task added offline ticks off X under it. The close goes with the refused
        // add, and the server never had X ticked off, so nothing it sends will take the tick off.
        var store = OneTask();
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var added = engine.AddItem(new JsonObject { ["content"] = "Added offline", ["project_id"] = "p", ["child_order"] = 1 });
        Assert.True(engine.IndentItem("x"));
        engine.CompleteItem(added);
        Assert.True(engine.Snapshot().Items.Single(i => i.Id == "x").Completed);

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        var x = engine.Snapshot().Items.Single(i => i.Id == "x");
        Assert.False(x.Completed);
        Assert.Null(x.ParentId);
        Assert.Equal(0, engine.PendingCount);
        Assert.False(NewEngine(store).Snapshot().Items.Single(i => i.Id == "x").Completed);
    }

    [Fact]
    public async Task A_task_moved_into_a_project_whose_add_is_refused_goes_back_with_its_sub_tasks()
    {
        // The move names the project added offline, and carried A's sub-tasks into it. All three
        // go back to the project and section they were in, still under the same parents.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" });
        engine.Load();

        var added = engine.AddProject("New");
        Assert.True(engine.MoveItemToProject("a", added));

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.All(["a", "b", "c"], id => Assert.Equal("p", items[id].ProjectId));
        Assert.All(["a", "b", "c"], id => Assert.Equal("s1", items[id].SectionId));
        Assert.Null(items["a"].ParentId);
        Assert.Equal("a", items["b"].ParentId);
        Assert.Equal("b", items["c"].ParentId);
        Assert.Equal(1, items["a"].ChildOrder);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task A_task_moved_into_a_section_of_a_project_whose_add_is_refused_goes_back_with_its_sub_tasks()
    {
        // The move names the section rather than the project, but the section was added in the
        // project added offline, so it goes with the project's add all the same.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" });
        engine.Load();

        var project = engine.AddProject("New");
        var section = engine.AddSection("Later", project);
        Assert.True(engine.MoveItemToSection("a", section));

        // Only the project's add is refused.
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.Where(c => c.Type == "project_add").ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected")),
        };
        await engine.SyncAsync();

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.All(["a", "b", "c"], id => Assert.Equal("p", items[id].ProjectId));
        Assert.All(["a", "b", "c"], id => Assert.Equal("s1", items[id].SectionId));
        Assert.Null(items["a"].ParentId);
        Assert.Equal("a", items["b"].ParentId);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task A_task_indented_then_reordered_under_one_whose_add_is_refused_goes_back_to_its_old_place()
    {
        // Indented, then moved below a sub-task added beside it. The reorder came after the indent,
        // and its priors have X first under the refused task. Put back after the indent rather
        // than before, that position would be left on X at the top level.
        var store = OneTask();
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var parent = engine.AddItem(new JsonObject { ["content"] = "Parent", ["project_id"] = "p", ["child_order"] = 1 });
        Assert.True(engine.IndentItem("x"));
        engine.AddItem(new JsonObject { ["content"] = "Child", ["project_id"] = "p", ["parent_id"] = parent, ["child_order"] = 2 });
        Assert.True(engine.MoveItem("x", 1));

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        var x = Assert.Single(engine.Snapshot().Items);
        Assert.Null(x.ParentId);
        Assert.Equal(2, x.ChildOrder);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task A_task_the_server_names_goes_back_when_its_indent_goes_with_a_refused_add()
    {
        // Both added offline, the second indented under the first. The server takes the second's add
        // and names it, and refuses the first's. The indent recorded the task under the id it had
        // here, and has to find it under the new one.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Projects(), new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.AddItem(new JsonObject { ["content"] = "Parent", ["project_id"] = "p", ["child_order"] = 1 });
        var moved = engine.AddItem(new JsonObject { ["content"] = "Moved", ["project_id"] = "p", ["child_order"] = 2 });
        Assert.True(engine.IndentItem(moved));

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(
                c => c.Uuid,
                c => c.TempId == moved ? new CommandResult(true, null, null) : new CommandResult(false, "ERR", "rejected")),
            TempIdMapping = new Dictionary<string, string> { [moved] = "real" },
        };
        await engine.SyncAsync();

        var task = Assert.Single(engine.Snapshot().Items);
        Assert.Equal("real", task.Id);
        Assert.Null(task.ParentId);
        Assert.Equal(2, task.ChildOrder);
    }

    [Fact]
    public async Task A_task_indented_twice_under_tasks_whose_adds_are_refused_goes_back_where_it_started()
    {
        // Indented under one task added offline, then under a sub-task added under that. Both moves
        // go with the refused add, and the second one's priors have X under the first task — so
        // rolled back in the order they were made, X ends up under a task that isn't there.
        var store = OneTask();
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var parent = engine.AddItem(new JsonObject { ["content"] = "Parent", ["project_id"] = "p", ["child_order"] = 1 });
        var child = engine.AddItem(new JsonObject { ["content"] = "Child", ["project_id"] = "p", ["parent_id"] = parent, ["child_order"] = 1 });
        Assert.True(engine.IndentItem("x"));
        Assert.True(engine.IndentItem("x"));
        Assert.Equal(child, engine.Snapshot().Items.Single(i => i.Id == "x").ParentId);

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        var x = Assert.Single(engine.Snapshot().Items);
        Assert.Equal("x", x.Id);
        Assert.Null(x.ParentId);
        Assert.Equal(2, x.ChildOrder);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public async Task A_task_whose_add_is_refused_stays_gone_after_being_indented()
    {
        // The indent names the task the add was refused for, so it's cancelled along with the add.
        // Putting back where that task was filed mustn't bring it back, here or in the store.
        var store = Projects();
        store.PutResource("items", "r", """{"id":"r","content":"R","project_id":"p","child_order":1}""");
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var added = engine.AddItem(new JsonObject { ["content"] = "Added offline", ["project_id"] = "p", ["child_order"] = 2 });
        Assert.True(engine.IndentItem(added));

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        Assert.Equal("r", Assert.Single(engine.Snapshot().Items).Id);
        Assert.Equal(0, engine.PendingCount);
        Assert.Equal("r", Assert.Single(NewEngine(store).Snapshot().Items).Id);
    }

    [Fact]
    public async Task A_refused_move_leaves_an_edit_made_since()
    {
        // Renamed after the move, and the renames land where the move doesn't. Put back whole, the
        // move's priors would undo them here and nowhere else.
        var store = Family();
        store.PutResource("items", "q1", """{"id":"q1","content":"Q1","project_id":"q","child_order":5}""");
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();

        engine.MoveItemToProject("a", "q");
        Assert.Equal(6, engine.Snapshot().Items.Single(i => i.Id == "a").ChildOrder);

        engine.UpdateItem("a", new JsonObject { ["content"] = "A renamed" });
        engine.UpdateItem("b", new JsonObject { ["content"] = "B renamed" });

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = RefusingMoves(commands) };
        await engine.SyncAsync();

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.Equal("A renamed", items["a"].Content);
        Assert.Equal("B renamed", items["b"].Content);
        Assert.All(["a", "b", "c"], id => Assert.Equal("p", items[id].ProjectId));
        Assert.All(["a", "b", "c"], id => Assert.Equal("s1", items[id].SectionId));
        Assert.Equal(1, items["a"].ChildOrder);
    }

    [Fact]
    public async Task A_refused_move_leaves_a_sub_task_under_the_parent_it_was_given_since()
    {
        // The move only took its sub-tasks into another project. C was outdented under A after
        // that, and the outdent lands where the move doesn't, so C stays under A.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();

        engine.MoveItemToProject("a", "q");
        Assert.True(engine.OutdentItem("c"));

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(
                c => c.Uuid,
                c => c.Args["id"]!.ToString() == "a" ? new CommandResult(false, "ERR", "rejected") : new CommandResult(true, null, null)),
        };
        await engine.SyncAsync();

        var c = engine.Snapshot().Items.Single(i => i.Id == "c");
        Assert.Equal("a", c.ParentId);
        Assert.Equal(2, c.ChildOrder);
        Assert.Equal("p", c.ProjectId);
        Assert.Equal("s1", c.SectionId);
    }

    [Fact]
    public async Task A_refused_outdent_puts_the_task_back_under_the_name_the_server_gave_its_parent()
    {
        // Both added offline, one under the other, and the sub-task then outdented. The server takes
        // both adds and refuses the outdent, so the parent it goes back under has been renamed.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Projects(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();

        var parent = engine.AddItem(new JsonObject { ["content"] = "Parent", ["project_id"] = "p" });
        var child = engine.AddItem(new JsonObject { ["content"] = "Child", ["project_id"] = "p", ["parent_id"] = parent });
        Assert.True(engine.OutdentItem(child));

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = RefusingMoves(commands),
            TempIdMapping = new Dictionary<string, string> { [parent] = "parent", [child] = "child" },
        };
        await engine.SyncAsync();

        var restored = Assert.Single(engine.Snapshot().Items, i => i.Content == "Child");
        Assert.Equal("child", restored.Id);
        Assert.Equal("parent", restored.ParentId);
    }

    [Fact]
    public async Task A_sub_task_the_server_names_while_its_move_is_pending_isnt_pulled_back()
    {
        // Added offline and carried by the move. The add lands first, and the server's copy of the
        // sub-task comes back under its new name, still in the old project.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 5);
        engine.Load();

        var temp = engine.AddItem(AddedUnder("a"));
        engine.MoveItemToProject("a", "q");

        // No verdict on the move, so it's still pending once this lands.
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.Where(c => c.Type == "item_add").ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = "real" },
            Changes = [Json.Change("items", "real", """{"id":"real","content":"Added offline","project_id":"p","section_id":"s1","parent_id":"a","child_order":2}""")],
        };
        await engine.SyncAsync();

        Assert.Equal(1, engine.PendingCount);
        Assert.Equal("q", engine.Snapshot().Items.Single(i => i.Id == "real").ProjectId);
    }

    [Fact]
    public async Task A_move_refused_after_a_restart_puts_back_a_sub_task_the_server_named_before_it()
    {
        // Named in one session, and the move refused in the next. Nothing remembers the made-up
        // name by then, so what the move recorded has to have the new one.
        var store = Family();
        await NameTheSubTaskOfAPendingMove(store);

        var api = new FakeApi { Next = commands => new SyncResponse { SyncToken = "s3", SyncStatus = Refused(commands) } };
        var restarted = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        restarted.Load();
        await restarted.SyncAsync();

        var added = Assert.Single(restarted.Snapshot().Items, i => i.Content == "Added offline");
        Assert.Equal("real", added.Id);
        Assert.Equal("p", added.ProjectId);
    }

    [Fact]
    public async Task After_a_restart_a_pending_move_still_holds_a_sub_task_the_server_named_before_it()
    {
        var store = Family();
        await NameTheSubTaskOfAPendingMove(store);

        // Still no verdict on the move, and the server's copy of the sub-task has it where it was.
        var api = new FakeApi
        {
            Next = _ => new SyncResponse
            {
                SyncToken = "s3",
                Changes = [Json.Change("items", "real", """{"id":"real","content":"Added offline","project_id":"p","section_id":"s1","parent_id":"a","child_order":2}""")],
            },
        };
        var restarted = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        restarted.Load();
        await restarted.SyncAsync();

        Assert.Equal(1, restarted.PendingCount);
        Assert.Equal("q", restarted.Snapshot().Items.Single(i => i.Id == "real").ProjectId);
    }

    [Fact]
    public async Task Two_refused_moves_of_a_task_leave_it_where_it_started()
    {
        // The second move's priors have the task where the first put it. Rolled back in the order
        // they were made, that's where it would be left, and the server would never say otherwise.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();

        engine.MoveItemToProject("a", "q");
        engine.MoveItemToProject("a", "other");

        api.Next = commands => new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.All(["a", "b", "c"], id => Assert.Equal("p", items[id].ProjectId));
        Assert.All(["a", "b", "c"], id => Assert.Equal("s1", items[id].SectionId));
        Assert.Equal(1, items["a"].ChildOrder);
        Assert.Equal(2, engine.FailedCount);
    }

    [Fact]
    public async Task A_move_made_while_an_earlier_one_was_on_its_way_goes_back_with_it_when_both_are_refused()
    {
        // The first is refused a round before the second is sent, so they're not rolled back
        // together — the second has to be told where the first found the task.
        var api = new FakeApi();
        var engine = new SyncEngine(api, Family(), new FakeSecrets { Stored = "tok" }, attemptCeiling: 1);
        engine.Load();
        engine.MoveItemToProject("a", "q");

        api.Next = commands =>
        {
            Assert.True(engine.MoveItemToProject("a", "other"));
            return new SyncResponse { SyncToken = "s2", SyncStatus = Refused(commands) };
        };
        await engine.SyncAsync();

        api.Next = commands => new SyncResponse { SyncToken = "s3", SyncStatus = Refused(commands) };
        await engine.SyncAsync();

        var items = engine.Snapshot().Items.ToDictionary(i => i.Id);
        Assert.All(["a", "b", "c"], id => Assert.Equal("p", items[id].ProjectId));
        Assert.All(["a", "b", "c"], id => Assert.Equal("s1", items[id].SectionId));
        Assert.Equal(2, engine.FailedCount);
    }

    [Fact]
    public void An_indent_can_be_reverted_back_to_where_it_was()
    {
        var engine = TwoSiblings();
        engine.IndentItem("b");

        engine.Revert(engine.Outbox.Single().Uuid);

        Assert.Null(engine.Snapshot().Items.Single(i => i.Id == "b").ParentId);
        Assert.Equal(0, engine.PendingCount);
    }

    // ---- Projects ------------------------------------------------------------------------------

    [Fact]
    public void Adding_a_project_shows_it_immediately_and_queues_project_add()
    {
        var engine = NewEngine(new InMemorySnapshotStore());

        var temp = engine.AddProject("Work");

        Assert.Equal("Work", engine.Snapshot().Projects.Single().Name);
        var cmd = engine.Outbox.Single();
        Assert.Equal("project_add", cmd.Type);
        Assert.Equal(temp, cmd.TempId);
        Assert.Equal("Work", Args(cmd)["name"]!.ToString());
    }

    [Fact]
    public void Renaming_and_favouriting_a_project_queue_updates()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        var engine = NewEngine(store);

        engine.RenameProject("p1", "Client work");
        engine.SetProjectFavorite("p1", true);

        Assert.Equal("Client work", engine.Snapshot().Projects.Single().Name);
        Assert.Equal(new[] { "project_update", "project_update" }, engine.Outbox.Select(c => c.Type).ToArray());
    }

    [Fact]
    public void Deleting_a_project_takes_its_tasks_and_sections_with_it()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Home"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p1"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"In Work","project_id":"p1"}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"In Home","project_id":"p2"}""");
        var engine = NewEngine(store);

        engine.DeleteProject("p1");

        var snapshot = engine.Snapshot();
        Assert.Equal("Home", snapshot.Projects.Single().Name);
        Assert.Equal("In Home", snapshot.Items.Single().Content);
        Assert.Empty(snapshot.Sections);
        Assert.Equal("project_delete", engine.Outbox.Single().Type);
    }

    [Fact]
    public void Deleting_a_project_takes_its_sub_projects_with_it()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Parent"}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Child","parent_id":"p1"}""");
        store.PutResource("projects", "p3", """{"id":"p3","name":"Grandchild","parent_id":"p2"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"In grandchild","project_id":"p3"}""");
        var engine = NewEngine(store);

        engine.DeleteProject("p1");

        // Leaving descendants behind orphans them: nothing can reach them, but their tasks still show.
        var snapshot = engine.Snapshot();
        Assert.Empty(snapshot.Projects);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void Reverting_a_project_delete_restores_what_was_inside_it()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p1"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"A task","project_id":"p1"}""");
        var engine = NewEngine(store);

        engine.DeleteProject("p1");
        engine.Revert(engine.Outbox.Single().Uuid);

        var snapshot = engine.Snapshot();
        Assert.Single(snapshot.Projects);
        Assert.Single(snapshot.Sections);
        Assert.Single(snapshot.Items);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Undo_of_a_project_delete_that_has_not_been_sent_restores_it()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Inside","project_id":"p1"}""");
        var engine = NewEngine(store);

        engine.DeleteProject("p1");

        Assert.True(engine.Undo());
        Assert.Single(engine.Snapshot().Projects);
        Assert.Single(engine.Snapshot().Items);
    }

    [Fact]
    public async Task Undo_after_the_server_has_the_project_delete_reverses_nothing_rather_than_something_else()
    {
        var api = new FakeApi();
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("items", "elsewhere", """{"id":"elsewhere","content":"Elsewhere"}""");
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        engine.DeleteItem("elsewhere");
        engine.DeleteProject("p1");

        api.Next = cmds => new SyncResponse
        {
            SyncToken = "s1",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };
        await engine.SyncAsync();

        // Todoist can't undelete a project, so Ctrl+Z must not quietly reverse the earlier delete.
        Assert.False(engine.Undo());
        Assert.Empty(engine.Snapshot().Items);

        // The earlier action is still there once the user asks again. It comes back as a new task,
        // since an acknowledged delete can only be reversed by recreating it.
        Assert.True(engine.Undo());
        Assert.Contains(engine.Snapshot().Items, i => i.Content == "Elsewhere");
    }

    [Fact]
    public void Deleting_an_unknown_project_queues_nothing()
    {
        var engine = NewEngine(new InMemorySnapshotStore());

        engine.DeleteProject("ghost");

        Assert.Equal(0, engine.PendingCount);
    }

    // ---- Sections ------------------------------------------------------------------------------

    [Fact]
    public void Adding_a_section_places_it_in_its_project()
    {
        var engine = NewEngine(new InMemorySnapshotStore());

        engine.AddSection("Reports", "p1");

        var section = engine.Snapshot().Sections.Single();
        Assert.Equal("Reports", section.Name);
        Assert.Equal("p1", section.ProjectId);
        Assert.Equal("section_add", engine.Outbox.Single().Type);
    }

    [Fact]
    public void Deleting_a_section_takes_its_tasks_with_it()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p1"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"In section","project_id":"p1","section_id":"s1"}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Loose","project_id":"p1"}""");
        var engine = NewEngine(store);

        engine.DeleteSection("s1");

        var snapshot = engine.Snapshot();
        Assert.Empty(snapshot.Sections);
        Assert.Equal("Loose", snapshot.Items.Single().Content);
    }

    [Fact]
    public void Renaming_a_section_queues_an_update()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p1"}""");
        var engine = NewEngine(store);

        engine.RenameSection("s1", "Paperwork");

        Assert.Equal("Paperwork", engine.Snapshot().Sections.Single().Name);
        Assert.Equal("section_update", engine.Outbox.Single().Type);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static SyncEngine NewEngine(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    private static SyncEngine TwoSiblings()
    {
        var store = Projects();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        return NewEngine(store);
    }

    private static SyncEngine ParentAndChild()
    {
        var store = Projects();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","parent_id":"a","child_order":1}""");
        return NewEngine(store);
    }

    /// <summary>A store holding the projects the helpers below file their tasks in and move them to.</summary>
    private static InMemorySnapshotStore Projects()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("projects", "q", """{"id":"q","name":"Home","child_order":2}""");
        store.PutResource("projects", "other", """{"id":"other","name":"Other","child_order":3}""");
        return store;
    }

    private static Dictionary<string, CommandResult> Refused(IReadOnlyList<Command> commands)
        => commands.ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected"));

    /// <summary>Refuses the moves in a batch and takes everything else.</summary>
    private static Dictionary<string, CommandResult> RefusingMoves(IReadOnlyList<Command> commands)
        => commands.ToDictionary(
            c => c.Uuid,
            c => c.Type == "item_move" ? new CommandResult(false, "ERR", "rejected") : new CommandResult(true, null, null));

    /// <summary>A store holding one top-level task, X, second in its project.</summary>
    private static InMemorySnapshotStore OneTask()
    {
        var store = Projects();
        store.PutResource("items", "x", """{"id":"x","content":"X","project_id":"p","child_order":2}""");
        return store;
    }

    /// <summary>A sub-task added offline under a task in <see cref="Family"/>.</summary>
    private static JsonObject AddedUnder(string parentId)
        => new() { ["content"] = "Added offline", ["project_id"] = "p", ["section_id"] = "s1", ["parent_id"] = parentId };

    /// <summary>
    /// A session that adds a sub-task under A offline and moves A to project q, and in which the
    /// server takes the add and names the sub-task "real" but says nothing about the move.
    /// </summary>
    private static async Task NameTheSubTaskOfAPendingMove(InMemorySnapshotStore store)
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var temp = engine.AddItem(AddedUnder("a"));
        engine.MoveItemToProject("a", "q");

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.Where(c => c.Type == "item_add").ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = "real" },
        };
        await engine.SyncAsync();

        Assert.Equal(1, engine.PendingCount);
    }

    /// <summary>A task with a child and a grandchild, in a section, beside one that's none of theirs.</summary>
    private static InMemorySnapshotStore Family()
    {
        var store = Projects();
        store.PutResource("sections", "s1", """{"id":"s1","name":"Reports","project_id":"p"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","section_id":"s1","parent_id":"a","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","section_id":"s1","parent_id":"b","child_order":1}""");
        store.PutResource("items", "n", """{"id":"n","content":"N","project_id":"p","section_id":"s1","child_order":2}""");
        return store;
    }

    private static JsonObject Args(OutboxCommand cmd) => (JsonObject)JsonNode.Parse(cmd.ArgsJson)!;
}
