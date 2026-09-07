using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Adding a task under another one.
/// </summary>
/// <remarks>
/// Sub-tasks could always be shown and never made: the only way to get one was to add a task, walk
/// it up the list until it sat directly under the intended parent, and indent it — because indenting
/// adopts the sibling above and nothing else. This is the way in.
/// </remarks>
public class SubtaskCreationTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>Held so the placement can be read off the model rather than off the row.</summary>
    private SyncEngine _engine = null!;

    /// <summary>A project with a section, so where a sub-task lands can be told from where it doesn't.</summary>
    private MainPresenter Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("projects", "q", """{"id":"q","name":"Home","child_order":2}""");
        store.PutResource("sections", "s", """{"id":"s","name":"Admin","project_id":"p"}""");
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","section_id":"s","child_order":1}""");
        store.PutResource("items", "z", """{"id":"z","content":"Elsewhere","project_id":"q","child_order":1}""");
        return All(store);
    }

    private static TaskRow Added(MainPresenter presenter, string? id)
    {
        Assert.NotNull(id);
        return presenter.Rows.Single(r => r.Id == id);
    }

    [Fact]
    public void The_new_task_is_filed_under_the_one_it_was_added_to()
    {
        var presenter = Seeded();

        var row = Added(presenter, presenter.AddSubtask("a", "Write it up"));

        Assert.Equal("Write it up", row.Content);
        Assert.Equal(1, row.Depth);
        Assert.True(presenter.Rows.Single(r => r.Id == "a").HasChildren);
    }

    [Fact]
    public void It_lands_in_the_parents_project_and_section()
    {
        // Not in whatever the outline happens to be showing, and not nowhere: a sub-task lives
        // where its parent lives, and Todoist would refuse it anywhere else.
        var presenter = Seeded();

        var id = presenter.AddSubtask("a", "Write it up");

        var item = _engine.Snapshot().Items.Single(i => i.Id == id);
        Assert.Equal("p", item.ProjectId);
        Assert.Equal("s", item.SectionId);
        Assert.Equal("a", item.ParentId);
    }

    [Fact]
    public void What_was_typed_is_read_the_way_quick_add_reads_it()
    {
        // A priority and a day are worth typing with the words rather than setting afterwards.
        var presenter = Seeded();

        var row = Added(presenter, presenter.AddSubtask("a", "Write it up p1 tomorrow"));

        Assert.Equal("Write it up", row.Content);
        Assert.Equal(Priority.P1, row.Priority);
        Assert.Equal(Today.AddDays(1), row.DueOn);
    }

    [Fact]
    public void A_project_named_in_the_text_does_not_move_it()
    {
        // The parent decides where a sub-task lives, so a "#Home" in the words is a place it cannot
        // go. Left in the content rather than acted on, so nothing the user typed disappears.
        var presenter = Seeded();

        var id = presenter.AddSubtask("a", "Write it up #Home");

        Assert.Equal("p", _engine.Snapshot().Items.Single(i => i.Id == id).ProjectId);
        Assert.Contains("Home", Added(presenter, id).Content);
    }

    [Fact]
    public void Adding_under_a_folded_task_opens_it()
    {
        // Otherwise the task is created out of sight, which is the one way this can look as though
        // it did nothing at all.
        var presenter = Seeded();
        presenter.AddSubtask("a", "First");
        presenter.SetCollapsed("a", true);

        var id = presenter.AddSubtask("a", "Second");

        Assert.False(presenter.IsCollapsed("a"));
        Assert.Contains(presenter.Rows, r => r.Id == id);
    }

    [Fact]
    public void A_sub_task_can_have_one_of_its_own()
    {
        var presenter = Seeded();
        var child = presenter.AddSubtask("a", "Child");

        var row = Added(presenter, presenter.AddSubtask(child!, "Grandchild"));

        Assert.Equal(2, row.Depth);
    }

    [Fact]
    public void Nothing_is_added_for_nothing_typed()
    {
        var presenter = Seeded();
        var before = presenter.Rows.Count;

        Assert.Null(presenter.AddSubtask("a", string.Empty));
        Assert.Null(presenter.AddSubtask("a", "   "));

        Assert.Equal(before, presenter.Rows.Count);
    }

    [Fact]
    public void A_parent_that_is_not_there_gets_nothing()
    {
        // The command is greyed without a task, but the palette reaches it and a task can go away
        // between the menu opening and the prompt being answered.
        var presenter = Seeded();

        Assert.Null(presenter.AddSubtask("no such task", "Write it up"));
    }

    private MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        _engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        _engine.Load();
        return new MainPresenter(_engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
    }

    private MainPresenter All(InMemorySnapshotStore store)
    {
        var presenter = NewPresenter(store);
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
