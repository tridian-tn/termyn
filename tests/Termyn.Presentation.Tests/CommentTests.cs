using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>What the comments pane is handed, and what it can ask for.</summary>
public class CommentTests
{
    private static readonly DateOnly Today = new(2026, 8, 14);

    [Fact]
    public void The_comments_on_a_task_come_back_ready_to_draw()
    {
        var presenter = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"the first thing said","posted_at":"2026-08-12T09:30:00Z"}"""));

        var row = Assert.Single(presenter.CommentsOn("i1"));

        Assert.Equal("n1", row.Id);
        Assert.Equal("the first thing said", row.Content);
        Assert.NotEqual(string.Empty, row.Posted);
        Assert.Null(row.AttachmentName);
    }

    [Fact]
    public void One_that_has_not_reached_the_server_says_nothing_about_when_it_was_posted()
    {
        // Which is how the pane knows to say so. Offline this is the ordinary state of a comment,
        // not a fault, and it may sit that way for a while.
        var presenter = Seeded();
        presenter.AddComment("i1", "written offline");

        Assert.Equal(string.Empty, Assert.Single(presenter.CommentsOn("i1")).Posted);
    }

    [Fact]
    public void An_unparseable_posted_time_is_left_blank_rather_than_guessed_at()
    {
        var presenter = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"odd","posted_at":"not a date"}"""));

        Assert.Equal(string.Empty, Assert.Single(presenter.CommentsOn("i1")).Posted);
    }

    [Fact]
    public void A_file_on_a_comment_is_named_even_with_nothing_said_alongside_it()
    {
        var presenter = Seeded((
            "notes",
            "n1",
            """{"id":"n1","item_id":"i1","content":"","file_attachment":{"file_name":"agenda.pdf"}}"""));

        Assert.Equal("agenda.pdf", Assert.Single(presenter.CommentsOn("i1")).AttachmentName);
    }

    // ---- Writing ---------------------------------------------------------------------------------

    [Fact]
    public void Adding_a_comment_shows_it_at_once()
    {
        var presenter = Seeded();

        Assert.True(presenter.AddComment("i1", "said it"));
        Assert.Equal("said it", Assert.Single(presenter.CommentsOn("i1")).Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Whitespace_is_not_a_comment(string text)
    {
        var presenter = Seeded();

        Assert.False(presenter.AddComment("i1", text));
        Assert.Empty(presenter.CommentsOn("i1"));
    }

    [Fact]
    public void A_comment_is_trimmed_of_what_the_box_left_round_it()
    {
        var presenter = Seeded();
        presenter.AddComment("i1", "  said it  \r\n");

        Assert.Equal("said it", Assert.Single(presenter.CommentsOn("i1")).Content);
    }

    [Fact]
    public void Commenting_on_nothing_at_all_is_declined_rather_than_queued()
    {
        var presenter = Seeded();

        Assert.False(presenter.AddComment(null, "into the void"));
        Assert.False(presenter.AddComment("no such task", "nor here"));
    }

    [Fact]
    public void Emptying_a_comment_is_not_an_edit()
    {
        // Deleting is a different action with a different consequence — it can't be undone. An edit
        // that empties the box must not quietly become one.
        var presenter = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"still means something"}"""));

        Assert.False(presenter.EditComment("n1", "   "));
        Assert.Equal("still means something", Assert.Single(presenter.CommentsOn("i1")).Content);
    }

    [Fact]
    public void Deleting_a_comment_takes_it_off_the_pane()
    {
        var presenter = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"regretted"}"""));

        presenter.DeleteComment("n1");

        Assert.Empty(presenter.CommentsOn("i1"));
    }

    // ---- Whether the box should be there at all --------------------------------------------------

    [Fact]
    public void A_task_and_a_project_can_both_be_commented_on()
    {
        var presenter = Seeded();

        Assert.True(presenter.CanCommentOn("i1"));
        Assert.True(presenter.CanCommentOn("p1"));
    }

    [Fact]
    public void Something_the_account_no_longer_holds_cannot()
    {
        var presenter = Seeded();

        Assert.False(presenter.CanCommentOn("gone"));
        Assert.False(presenter.CanCommentOn(null));
    }

    // ---- The mark in the outline ------------------------------------------------------------------

    [Fact]
    public void A_task_carries_how_many_comments_are_on_it()
    {
        // Only the count. Without it the outline gives no sign a task has a conversation on it, and
        // carrying the comments themselves would put them on every row on every publish.
        var presenter = Seeded(
            ("notes", "n1", """{"id":"n1","item_id":"i1","content":"one"}"""),
            ("notes", "n2", """{"id":"n2","item_id":"i1","content":"two"}"""));

        Assert.Equal(2, presenter.Rows.Single(r => r.Id == "i1").CommentCount);
    }

    [Fact]
    public void A_task_with_no_comments_says_none_rather_than_nothing()
        => Assert.Equal(0, Seeded().Rows.Single(r => r.Id == "i1").CommentCount);

    [Fact]
    public void A_project_comment_is_not_counted_against_a_task()
    {
        var presenter = Seeded(("project_notes", "pn1", """{"id":"pn1","project_id":"p1","content":"on the project"}"""));

        Assert.Equal(0, presenter.Rows.Single(r => r.Id == "i1").CommentCount);
        Assert.Single(presenter.CommentsOn("p1"));
    }

    [Fact]
    public void Comments_are_not_carried_on_every_row()
    {
        // The same reasoning as the description: only the selected owner's are ever on screen, and
        // the outline projects every row on every publish.
        Assert.DoesNotContain(
            typeof(TaskRow).GetProperties(),
            p => p.PropertyType != typeof(int) && p.Name.Contains("Comment", StringComparison.Ordinal));
    }

    // ---- Setup ------------------------------------------------------------------------------------

    private static MainPresenter Seeded(params (string Type, string Id, string Json)[] resources)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Write it up","project_id":"p1"}""");
        store.PutResource("projects", "p1", """{"id":"p1","name":"Home"}""");

        foreach (var (type, id, json) in resources)
            store.PutResource(type, id, json);

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
