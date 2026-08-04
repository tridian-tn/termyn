using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>Reading and writing the notes on a task.</summary>
public class DescriptionTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public void The_notes_on_a_task_are_the_markdown_the_account_holds()
    {
        // Markdown, not a rendering of it: Todoist's own editor is a rich-text skin over the same
        // text, and what arrives here is the source.
        var presenter = Seeded("""Line one\n\n**bold** and a [link](https://example.com)""");

        Assert.Equal("Line one\n\n**bold** and a [link](https://example.com)", presenter.DescriptionOf("i1"));
    }

    [Fact]
    public void A_task_with_no_notes_has_empty_ones_rather_than_none()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Bare","project_id":"p"}""");

        Assert.Equal(string.Empty, Presenter(store).DescriptionOf("i1"));
    }

    [Fact]
    public void A_task_the_account_no_longer_holds_has_empty_notes()
    {
        Assert.Equal(string.Empty, Seeded("Anything").DescriptionOf("gone"));
        Assert.Equal(string.Empty, Seeded("Anything").DescriptionOf(null));
    }

    [Fact]
    public void Writing_the_notes_queues_the_edit_and_shows_it_at_once()
    {
        var presenter = Seeded("Before");

        presenter.SetDescription("i1", "After");

        Assert.Equal("After", presenter.DescriptionOf("i1"));
    }

    [Fact]
    public void Writing_the_notes_queues_one_field_and_no_others()
    {
        // The name of the test above claims a queued command; reading the description back only
        // proves the local copy moved. This is the half that reaches the account — and a command
        // carrying more than the one field would put a stale copy of everything else with it.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Write it up","project_id":"p","description":"Before"}""");

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));

        presenter.SetDescription("i1", "After");

        var queued = Assert.Single(engine.Outbox);
        Assert.Equal("item_update", queued.Type);

        var args = System.Text.Json.Nodes.JsonNode.Parse(queued.ArgsJson)!.AsObject();
        Assert.Equal("After", args["description"]!.ToString());
        Assert.Equal("i1", args["id"]!.ToString());
        Assert.Equal(["description", "id"], args.Select(a => a.Key).Order().ToArray());
    }

    [Fact]
    public void Writing_the_notes_changes_nothing_else_about_the_task()
    {
        // A patch, not a replacement: the update carries one field, and everything the row shows
        // has to come through it untouched.
        var presenter = Seeded("Before");
        var before = presenter.Rows.Single();

        presenter.SetDescription("i1", "After");

        Assert.Equal(before, presenter.Rows.Single());
    }

    [Fact]
    public void Notes_survive_the_task_being_deleted_and_undone()
    {
        // Undo recreates the task from what was held before it went, and the description is one of
        // the fields that may be sent back — so it has to still be there afterwards.
        var presenter = Seeded("Worth keeping");

        presenter.Delete("i1");
        Assert.True(presenter.Undo());

        Assert.Equal("Worth keeping", presenter.DescriptionOf("i1"));
    }

    [Fact]
    public void Saving_the_notes_over_and_over_leaves_nothing_to_undo()
    {
        // This is what makes an idle save safe to repeat. An item_update records nothing on the
        // undo stack — only a completion or a delete does — so a long editing session can't fill
        // Ctrl+Z with description edits.
        var presenter = Seeded("Before");

        presenter.SetDescription("i1", "One");
        presenter.SetDescription("i1", "Two");
        presenter.SetDescription("i1", "Three");

        Assert.False(presenter.CanUndo);
    }

    [Fact]
    public void A_completion_is_still_what_undo_reverses_after_a_run_of_saves()
    {
        // The other half of it: a save must not push the thing the user actually wants back out of
        // reach, however many of them happen in between.
        var presenter = Seeded("Before");
        presenter.Complete("i1");

        presenter.SetDescription("i1", "One");
        presenter.SetDescription("i1", "Two");

        Assert.True(presenter.Undo());
        Assert.Contains(presenter.Rows, r => r.Id == "i1" && !r.Completed);
    }

    [Fact]
    public void Undoing_a_completion_takes_the_task_back_whole_notes_and_all()
    {
        // Recorded because it surprised me, not because it is new: undo restores the task as it
        // stood before the completion, so anything changed after it goes back too. True of every
        // field rather than of notes in particular — priority and due date behave the same way —
        // and the sequence is an odd one. Left alone here; changing it means changing what undo
        // means, which is a good deal more than a notes panel.
        var presenter = Seeded("Before");
        presenter.Complete("i1");
        presenter.SetDescription("i1", "Written after it was ticked off");

        presenter.Undo();

        Assert.Equal("Before", presenter.DescriptionOf("i1"));
    }

    [Fact]
    public async Task A_completed_task_pulled_out_of_the_archive_still_shows_its_notes()
    {
        // Those are held apart from the live model, which is all the lookup used to consult — so a
        // completed task with notes on it showed a blank box, which reads as having none.
        var done = Json.Change(
            "items",
            "c1",
            """{"id":"c1","content":"Booked","project_id":"p","checked":true,"description":"what was agreed","completed_at":"2026-07-30T09:00:00Z"}""");

        var presenter = await WithCompleted(done);

        Assert.Contains(presenter.Rows, r => r.Id == "c1");
        Assert.Equal("what was agreed", presenter.DescriptionOf("c1"));
    }

    [Fact]
    public async Task A_completed_task_out_of_the_archive_is_not_one_the_account_still_holds()
    {
        // Which is what the view asks before it lets the box be typed into: an edit to one of these
        // is declined rather than queued, and a box that took the typing and dropped it on the
        // floor would be worse than one that never took it.
        var done = Json.Change(
            "items",
            "c1",
            """{"id":"c1","content":"Booked","project_id":"p","checked":true,"description":"what was agreed"}""");

        var presenter = await WithCompleted(done);

        Assert.False(presenter.HasTask("c1"));
        Assert.True(presenter.HasTask("i1"));
        Assert.False(presenter.HasTask(null));
    }

    [Fact]
    public void Writing_notes_to_a_task_the_account_no_longer_holds_queues_nothing()
    {
        // The path taken when the selected row disappears mid-edit: the save runs before the box is
        // reopened on whatever is selected now.
        var presenter = Seeded("Before");

        presenter.SetDescription("gone", "typed after it vanished");

        Assert.False(presenter.CanUndo);
        Assert.Equal(string.Empty, presenter.DescriptionOf("gone"));
    }

    [Fact]
    public void Notes_are_not_carried_on_every_row()
    {
        // Deliberate: a description runs to thousands of characters, only the selected task's is
        // ever on screen, and the outline projects every row on every publish.
        Assert.DoesNotContain(
            typeof(TaskRow).GetProperties(),
            p => p.Name.Contains("escription", StringComparison.Ordinal));
    }

    /// <summary>The seeded task, plus a completed one the archive hands back.</summary>
    private static async Task<MainPresenter> WithCompleted(ResourceChange done)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Live","project_id":"p"}""");

        var api = new FakeApi
        {
            Response = new SyncResponse { SyncToken = "s1" },
            Completed = _ => new CompletedPage([done], null),
        };

        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        await presenter.ToggleCompletedAsync();
        return presenter;
    }

    private static MainPresenter Seeded(string description)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource(
            "items",
            "i1",
            new JsonObject
            {
                ["id"] = "i1",
                ["content"] = "Write it up",
                ["project_id"] = "p",
                ["description"] = description.Replace("\\n", "\n"),
            }.ToJsonString());

        return Presenter(store);
    }

    private static MainPresenter Presenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
