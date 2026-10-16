using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// Drops in the sidebar: a project or section put down beside another, and a task put down on a
/// project or section.
/// </summary>
/// <remarks>
/// Every drop goes through a helper that asks first, the way a drag does on its way over, and
/// checks the answer matches what the drop then does.
/// </remarks>
public class StructureDropTests
{
    // ---- Projects ------------------------------------------------------------------------------

    [Fact]
    public void A_project_put_down_before_a_sibling_moves_up_to_it()
    {
        var engine = Structure();

        Assert.True(Project(engine, "p3", "p1", after: false));

        Assert.Equal(["p3", "p1", "p2"], Projects(engine));
        Assert.Equal("project_reorder", engine.Outbox.Single().Type);
    }

    [Fact]
    public void A_project_put_down_after_a_sibling_moves_down_to_it()
    {
        var engine = Structure();

        Assert.True(Project(engine, "p1", "p3", after: true));

        Assert.Equal(["p2", "p3", "p1"], Projects(engine));
    }

    [Fact]
    public void A_project_put_down_where_it_already_is_sends_nothing()
    {
        var engine = Structure();

        Assert.False(Project(engine, "p2", "p1", after: true));
        Assert.False(Project(engine, "p2", "p3", after: false));
        Assert.False(Project(engine, "p2", "p2", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_project_is_not_put_down_beside_one_under_another_parent()
    {
        // Putting it under another project is a different command, and nothing offers it yet.
        var engine = Structure();

        Assert.False(Project(engine, "sub", "p2", after: true));
        Assert.False(Project(engine, "p2", "sub", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void The_inbox_is_neither_moved_nor_put_down_beside()
    {
        // Todoist keeps it at the top whatever its order says.
        var engine = Structure();

        Assert.False(Project(engine, "in", "p1", after: false));
        Assert.False(Project(engine, "p1", "in", after: false));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void An_archived_project_is_nowhere_to_put_one_down()
    {
        var engine = Structure();

        Assert.False(Project(engine, "p1", "old", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    // ---- Sections ------------------------------------------------------------------------------

    [Fact]
    public void A_section_put_down_beside_another_in_its_project_moves_to_it()
    {
        var engine = Structure();

        Assert.True(Section(engine, "s3", "s1", after: false));
        Assert.Equal(["s3", "s1", "s2"], Sections(engine));

        Assert.True(Section(engine, "s3", "s2", after: true));
        Assert.Equal(["s1", "s2", "s3"], Sections(engine));
        Assert.All(engine.Outbox, c => Assert.Equal("section_reorder", c.Type));
    }

    [Fact]
    public void A_section_put_down_where_it_already_is_sends_nothing()
    {
        var engine = Structure();

        Assert.False(Section(engine, "s2", "s1", after: true));
        Assert.False(Section(engine, "s2", "s3", after: false));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_section_is_not_put_down_beside_one_in_another_project()
    {
        // Moving a section to another project is a different command too.
        var engine = Structure();

        Assert.False(Section(engine, "s1", "elsewhere", after: true));
        Assert.Equal(0, engine.PendingCount);
    }

    // ---- A task onto a project or section ------------------------------------------------------

    [Fact]
    public void A_task_dropped_on_another_project_moves_there()
    {
        var engine = Structure();

        Assert.True(ToProject(engine, "a", "p2"));
        Assert.Equal("p2", engine.Snapshot().Items.Single(i => i.Id == "a").ProjectId);
    }

    [Fact]
    public void A_task_dropped_on_the_project_it_is_already_top_level_in_stays_put()
    {
        var engine = Structure();

        Assert.False(ToProject(engine, "a", "p1"));
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_sub_task_dropped_on_its_own_project_comes_out_from_under_its_parent()
    {
        var engine = Structure();

        Assert.True(ToProject(engine, "under", "p1"));
        Assert.Null(engine.Snapshot().Items.Single(i => i.Id == "under").ParentId);
    }

    [Fact]
    public void A_task_dropped_on_a_section_moves_into_it_unless_it_is_already_there()
    {
        var engine = Structure();

        Assert.True(ToSection(engine, "a", "s2"));
        Assert.Equal("s2", engine.Snapshot().Items.Single(i => i.Id == "a").SectionId);

        Assert.False(ToSection(engine, "a", "s2"));
    }

    [Fact]
    public void Nowhere_the_model_lacks_takes_a_task()
    {
        var engine = Structure();

        Assert.False(ToProject(engine, "a", "ghost"));
        Assert.False(ToSection(engine, "a", "ghost"));
        Assert.False(ToProject(engine, "ghost", "p2"));
        Assert.Equal(0, engine.PendingCount);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static bool Project(SyncEngine engine, string id, string besideId, bool after)
    {
        var offered = engine.CanPlaceProject(id, besideId, after);
        var placed = engine.PlaceProject(id, besideId, after);

        Assert.Equal(offered, placed);
        return placed;
    }

    private static bool Section(SyncEngine engine, string id, string besideId, bool after)
    {
        var offered = engine.CanPlaceSection(id, besideId, after);
        var placed = engine.PlaceSection(id, besideId, after);

        Assert.Equal(offered, placed);
        return placed;
    }

    private static bool ToProject(SyncEngine engine, string id, string projectId)
    {
        var offered = engine.CanMoveItemToProject(id, projectId);
        var moved = engine.MoveItemToProject(id, projectId);

        Assert.Equal(offered, moved);
        return moved;
    }

    private static bool ToSection(SyncEngine engine, string id, string sectionId)
    {
        var offered = engine.CanMoveItemToSection(id, sectionId);
        var moved = engine.MoveItemToSection(id, sectionId);

        Assert.Equal(offered, moved);
        return moved;
    }

    /// <summary>The top-level projects that move, in the order they're held.</summary>
    private static string[] Projects(SyncEngine engine)
        => engine.Snapshot().Projects
            .Where(p => p.ParentId is null && !p.IsInboxProject && !p.IsArchived)
            .OrderBy(p => p.ChildOrder)
            .Select(p => p.Id)
            .ToArray();

    /// <summary>The sections of "p1", in the order they're held.</summary>
    private static string[] Sections(SyncEngine engine)
        => engine.Snapshot().Sections
            .Where(s => s.ProjectId == "p1")
            .OrderBy(s => s.SectionOrder)
            .Select(s => s.Id)
            .ToArray();

    /// <summary>
    /// The Inbox, three top-level projects, one under the first, one archived; three sections in the
    /// first project and one in the second; and a task in the first project with a sub-task.
    /// </summary>
    private static SyncEngine Structure()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "in", """{"id":"in","name":"Inbox","is_inbox_project":true,"child_order":0}""");
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Home","child_order":2}""");
        store.PutResource("projects", "p3", """{"id":"p3","name":"Chores","child_order":3}""");
        store.PutResource("projects", "sub", """{"id":"sub","name":"Admin","parent_id":"p1","child_order":1}""");
        store.PutResource("projects", "old", """{"id":"old","name":"Old","child_order":4,"is_archived":true}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Next","project_id":"p1","section_order":1}""");
        store.PutResource("sections", "s2", """{"id":"s2","name":"Later","project_id":"p1","section_order":2}""");
        store.PutResource("sections", "s3", """{"id":"s3","name":"Someday","project_id":"p1","section_order":3}""");
        store.PutResource("sections", "elsewhere", """{"id":"elsewhere","name":"Garden","project_id":"p2","section_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p1","child_order":1}""");
        store.PutResource("items", "under", """{"id":"under","content":"Under A","project_id":"p1","parent_id":"a","child_order":1}""");

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }
}
