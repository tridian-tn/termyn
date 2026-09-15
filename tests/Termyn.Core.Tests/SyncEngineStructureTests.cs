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
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        return NewEngine(store);
    }

    private static SyncEngine ParentAndChild()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","parent_id":"a","child_order":1}""");
        return NewEngine(store);
    }

    /// <summary>A task with a child and a grandchild, in a section, beside one that's none of theirs.</summary>
    private static InMemorySnapshotStore Family()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("sections", "s1", """{"id":"s1","name":"Reports","project_id":"p"}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","section_id":"s1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","section_id":"s1","parent_id":"a","child_order":1}""");
        store.PutResource("items", "c", """{"id":"c","content":"C","project_id":"p","section_id":"s1","parent_id":"b","child_order":1}""");
        store.PutResource("items", "n", """{"id":"n","content":"N","project_id":"p","section_id":"s1","child_order":2}""");
        return store;
    }

    private static JsonObject Args(OutboxCommand cmd) => (JsonObject)JsonNode.Parse(cmd.ArgsJson)!;
}
