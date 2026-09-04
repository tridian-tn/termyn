using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// What the user is told about a write the server refused, and what letting it go does.
/// </summary>
/// <remarks>
/// A command the server keeps refusing stops being retried and stays in the outbox, which is what
/// keeps the count in the status bar honest. Until there was something to read it with, that count
/// was also all there was: a permanent "1 failed" naming nothing and explaining nothing.
/// </remarks>
public class FailuresTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public async Task Nothing_refused_is_nothing_to_read()
    {
        var (presenter, _, _) = await Loaded();

        Assert.Empty(presenter.FailedChanges);
    }

    [Fact]
    public async Task A_refused_change_names_the_task_and_what_the_server_said()
    {
        var (presenter, engine, api) = await Loaded();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "Task renamed" });
        await Refuse(presenter, engine, api);

        var failure = Assert.Single(presenter.FailedChanges);

        Assert.Equal("Changing a task", failure.Change);
        Assert.Equal("Task", failure.Subject);
        Assert.Equal("rejected", failure.Reason);

        // The engine put this one back when it gave up on it, so there is nothing of the user's
        // left in it to lose.
        Assert.False(failure.DiscardsWork);
    }

    [Fact]
    public async Task A_refused_change_is_named_after_the_task_as_it_stands_again()
    {
        // Not after what the change would have made it. The rename was rolled back when the command
        // failed, so the row on screen still says "Task" and this has to agree with it — naming the
        // task by a title that is nowhere in the window is no use for finding it.
        var (presenter, engine, api) = await Loaded();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "Task renamed" });
        await Refuse(presenter, engine, api);

        Assert.Equal("Task", Assert.Single(presenter.FailedChanges).Subject);
    }

    [Fact]
    public async Task A_refused_creation_says_that_letting_it_go_costs_something()
    {
        var (presenter, engine, api) = await Loaded();

        engine.AddItem(new JsonObject { ["content"] = "Buy milk", ["project_id"] = "p1" });
        await Ignore(presenter, engine, api);

        var failure = Assert.Single(presenter.FailedChanges);

        Assert.Equal("Adding a task", failure.Change);
        Assert.Equal("Buy milk", failure.Subject);

        // The one that isn't free. The engine kept what was typed when the command failed, so this
        // is the only copy of it anywhere and dismissing is what takes it away.
        Assert.True(failure.DiscardsWork);
    }

    [Fact]
    public async Task Letting_a_refused_change_go_takes_it_off_the_count()
    {
        var (presenter, engine, api) = await Loaded();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "Task renamed" });
        await Refuse(presenter, engine, api);
        Assert.Equal(1, engine.FailedCount);

        presenter.DismissFailure(presenter.FailedChanges[0].Uuid);

        Assert.Equal(0, engine.FailedCount);
        Assert.Empty(presenter.FailedChanges);

        // And the task is still where it was, which the rollback had already seen to.
        Assert.Equal("Task", presenter.Rows.Single(r => r.Id == "i1").Content);
    }

    [Fact]
    public async Task Letting_a_refused_creation_go_takes_the_task_with_it()
    {
        // Which is the whole of why it is worth saying so before it happens.
        var (presenter, engine, api) = await Loaded();

        engine.AddItem(new JsonObject { ["content"] = "Buy milk", ["project_id"] = "p1" });
        await Ignore(presenter, engine, api);
        Assert.Contains(presenter.Rows, r => r.Content == "Buy milk");

        presenter.DismissFailure(presenter.FailedChanges[0].Uuid);

        Assert.Equal(0, engine.FailedCount);
        Assert.DoesNotContain(presenter.Rows, r => r.Content == "Buy milk");
    }

    [Fact]
    public async Task Two_refusals_are_both_readable_and_go_one_at_a_time()
    {
        var (presenter, engine, api) = await Loaded();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "Task renamed" });
        engine.AddItem(new JsonObject { ["content"] = "Buy milk", ["project_id"] = "p1" });
        await Ignore(presenter, engine, api);

        Assert.Equal(2, presenter.FailedChanges.Count);

        presenter.DismissFailure(presenter.FailedChanges[0].Uuid);

        var left = Assert.Single(presenter.FailedChanges);
        Assert.Equal("Adding a task", left.Change);
    }

    [Fact]
    public void A_command_nobody_has_written_a_line_for_is_shown_as_itself()
    {
        // Rather than as a guess. A name nobody recognises is still better than a confident
        // description of the wrong thing, and it says plainly that the list wants a line adding.
        var command = new OutboxCommand
        {
            Uuid = "u1",
            Type = "filter_add",
            ArgsJson = """{"id":"f1"}""",
            State = OutboxState.Failed,
            LastError = "no",
        };

        var failure = Assert.Single(Failures.From([command], Empty()));

        Assert.Equal("filter_add", failure.Change);
    }

    [Fact]
    public void A_command_naming_nothing_that_exists_is_left_unnamed()
    {
        // A deletion that failed after the thing had gone, say. No name at all beats a wrong one.
        var command = new OutboxCommand
        {
            Uuid = "u1",
            Type = "item_delete",
            ArgsJson = """{"id":"gone"}""",
            State = OutboxState.Failed,
            LastError = "no",
        };

        Assert.Null(Assert.Single(Failures.From([command], Empty())).Subject);
    }

    [Fact]
    public void A_server_that_said_nothing_is_reported_as_saying_nothing()
    {
        // Rather than as an empty pair of quotes, which reads as a reason of no characters.
        var command = new OutboxCommand
        {
            Uuid = "u1",
            Type = "item_delete",
            ArgsJson = """{"id":"i1"}""",
            State = OutboxState.Failed,
            LastError = "   ",
        };

        Assert.Null(Assert.Single(Failures.From([command], Empty())).Reason);
    }

    [Fact]
    public void Unreadable_arguments_leave_it_unnamed_rather_than_throwing()
    {
        // The arguments are stored as text and read back, so a row written by an older build is
        // reachable here — and the window this feeds is the one the user opened to clear a failure.
        var command = new OutboxCommand
        {
            Uuid = "u1",
            Type = "item_update",
            ArgsJson = "{ this is not json",
            State = OutboxState.Failed,
            LastError = "no",
        };

        Assert.Null(Assert.Single(Failures.From([command], Empty())).Subject);
    }

    [Fact]
    public void A_failure_reads_as_what_was_being_done_and_to_what()
    {
        var failure = new FailedChange("u1", "Changing a task", "Buy milk", "no", false, false);

        Assert.Equal("Changing a task — Buy milk", failure.ToString());
    }

    [Fact]
    public void One_with_nothing_to_name_reads_as_the_change_on_its_own()
    {
        // Rather than trailing a dash with nothing after it, which reads as a name that failed to
        // load rather than as one there was never going to be.
        var failure = new FailedChange("u1", "Deleting a task", null, "no", false, false);

        Assert.Equal("Deleting a task", failure.ToString());
    }

    [Fact]
    public void Only_what_the_server_has_finished_with_is_listed()
    {
        // A command still being retried is not a failure yet, and offering it to be dismissed would
        // let the user throw away a write that was about to land.
        var pending = new OutboxCommand { Uuid = "u1", Type = "item_update", ArgsJson = "{}" };
        var failed = new OutboxCommand { Uuid = "u2", Type = "item_update", ArgsJson = "{}", State = OutboxState.Failed };

        Assert.Equal(["u2"], Failures.From([pending, failed], Empty()).Select(f => f.Uuid));
    }

    // ---- Fixtures --------------------------------------------------------------------------------

    private static ModelSnapshot Empty()
        => new([], [], [], [], [], [], null, Today, TimeZoneInfo.Utc, 0, 0, [], new Dictionary<string, int>());

    /// <summary>A presenter over one project and one task, with nothing refused yet.</summary>
    private static async Task<(MainPresenter Presenter, SyncEngine Engine, FakeApi Api)> Loaded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Task","project_id":"p1"}""");

        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var clock = new FixedClock(Today);

        // Two attempts, so a refusal reaches the ceiling in the two syncs below rather than in ten.
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, clock, attemptCeiling: 2);
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
        presenter.Select(ViewSelection.Of(SmartView.All));
        await presenter.LoadAsync();

        return (presenter, engine, api);
    }

    /// <summary>Has the server refuse everything queued, until the engine gives up on it.</summary>
    private static async Task Refuse(MainPresenter presenter, SyncEngine engine, FakeApi api)
    {
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected")),
        };

        await engine.SyncAsync();
        await engine.SyncAsync();

        // The window is told by the sync; here the projection is asked for again directly, so the
        // snapshot the failures are named from is the one the rollback left behind.
        await presenter.LoadAsync();
    }

    /// <summary>
    /// Has the server say nothing at all about what it was sent, until the engine gives up waiting.
    /// </summary>
    /// <remarks>
    /// The only way a creation reaches the failed state. A creation the server <em>refuses</em> is
    /// cancelled outright — taken off the queue and out of the model there and then — because a
    /// refusal is a definite answer. One it never rules on is not: the account may well have the
    /// task, so what was typed is kept and the user is left to decide.
    /// </remarks>
    private static async Task Ignore(MainPresenter presenter, SyncEngine engine, FakeApi api)
    {
        api.Next = _ => new SyncResponse { SyncToken = "s2" };

        await engine.SyncAsync();
        await engine.SyncAsync();
        await presenter.LoadAsync();
    }

    [Fact]
    public void A_comment_and_a_reminder_are_named_after_the_task_they_are_on()
    {
        // Their own ids name a note and a reminder, neither of which has ever been on screen. What
        // the user would recognise is the task, which is what the arguments carry.
        var comment = new OutboxCommand
        {
            Uuid = "u1",
            Type = "note_add",
            TempId = "tmp-note",
            ArgsJson = """{"item_id":"i1","content":"a thought"}""",
            State = OutboxState.Failed,
        };

        var reminder = new OutboxCommand
        {
            Uuid = "u2",
            Type = "reminder_add",
            TempId = "tmp-rem",
            ArgsJson = """{"item_id":"i1"}""",
            State = OutboxState.Failed,
        };

        var failures = Failures.From([comment, reminder], WithTask());

        Assert.Equal("Adding a comment — Task", failures[0].ToString());
        Assert.Equal("Adding a reminder — Task", failures[1].ToString());
    }

    [Fact]
    public void A_comment_on_a_project_is_named_after_the_project()
    {
        var comment = new OutboxCommand
        {
            Uuid = "u1",
            Type = "note_add",
            TempId = "tmp-note",
            ArgsJson = """{"project_id":"p1","content":"a thought"}""",
            State = OutboxState.Failed,
        };

        Assert.Equal("Work", Assert.Single(Failures.From([comment], WithTask())).Subject);
    }

    [Fact]
    public void A_refusal_and_a_silence_are_told_apart()
    {
        // Only a refusal says where the change ended up. Reading a silence as a refusal would have
        // the window tell someone their account doesn't have something it may well have.
        var refused = new OutboxCommand
        {
            Uuid = "u1", Type = "item_update", ArgsJson = """{"id":"i1"}""", State = OutboxState.Failed,
        };

        var unanswered = new OutboxCommand
        {
            Uuid = "u2", Type = "item_update", ArgsJson = """{"id":"i1"}""", State = OutboxState.Failed,
            NoVerdictRounds = 2,
        };

        var failures = Failures.From([refused, unanswered], WithTask());

        Assert.False(failures[0].Unruled);
        Assert.True(failures[1].Unruled);
    }

    [Fact]
    public async Task A_creation_the_server_never_answered_about_is_marked_unruled()
    {
        // The path that actually produces one, rather than a command built by hand: it is the only
        // way a creation reaches this list at all.
        var (presenter, engine, api) = await Loaded();

        engine.AddItem(new JsonObject { ["content"] = "Buy milk", ["project_id"] = "p1" });
        await Ignore(presenter, engine, api);

        var failure = Assert.Single(presenter.FailedChanges);

        Assert.True(failure.Unruled);
        Assert.True(failure.DiscardsWork);
    }

    /// <summary>One project and one task, for naming things against.</summary>
    private static ModelSnapshot WithTask()
        => new(
            [new TaskItem { Id = "i1", Content = "Task", ProjectId = "p1" }],
            [new Project { Id = "p1", Name = "Work" }],
            [], [], [], [], null, Today, TimeZoneInfo.Utc, 0, 0, [], new Dictionary<string, int>());
}
