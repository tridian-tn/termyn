using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Reconnecting once Todoist has turned the token away: the account held read-only meanwhile, and
/// what a new token does depending on whose it is.
/// </summary>
/// <remarks>
/// The engine's half — the token, the cache, the queue, the wipe — is tested with the engine. This is
/// the half the window reads: the status, what's greyed, and the question asked before a different
/// account's token takes this one's place.
/// </remarks>
public class ReconnectTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public async Task The_same_accounts_token_lifts_the_hold_and_sends_what_was_held()
    {
        var (presenter, api, secrets) = await TurnedAway(edit: true);
        Assert.True(presenter.ReconnectNeeded);

        Assert.True(presenter.IsSameAccount("u1"));
        presenter.Reconnect("new", "u1");

        Assert.False(presenter.ReconnectNeeded);
        Assert.Equal("new", secrets.Stored);
        Assert.NotEqual(SyncState.ReconnectNeeded, presenter.SyncStatus.State);

        await presenter.SyncAsync();
        Assert.Equal("item_update", api.LastCommands.Single().Type);
    }

    [Fact]
    public async Task Another_accounts_token_is_not_the_same_account()
    {
        var (presenter, _, _) = await TurnedAway();

        Assert.False(presenter.IsSameAccount("u2"));
        Assert.False(presenter.IsSameAccount(null));
    }

    [Fact]
    public async Task The_question_before_switching_counts_what_switching_would_lose()
    {
        var (none, _, _) = await TurnedAway();
        var (one, _, _) = await TurnedAway(edit: true);

        Assert.DoesNotContain("hasn't reached Todoist", none.SwitchAccountQuestion("u2"));
        Assert.Contains("1 change hasn't reached Todoist yet", one.SwitchAccountQuestion("u2"));
    }

    [Fact]
    public async Task A_token_whose_account_wasnt_named_isnt_called_a_different_account()
    {
        // It may well be this account's own, so the question mustn't say otherwise.
        var (presenter, _, _) = await TurnedAway(edit: true);

        var question = presenter.SwitchAccountQuestion(null);

        Assert.DoesNotContain("is for a different Todoist account", question);
        Assert.Contains("can't tell whether it's this one", question);
        Assert.Contains("1 change hasn't reached Todoist yet", question);
    }

    [Fact]
    public async Task Comments_and_drags_are_held_while_the_token_is_turned_away()
    {
        // A comment box empties as it sends, so a refused comment would take the typing with it.
        var (presenter, _, _) = await TurnedAway();
        presenter.Select(ViewSelection.OfProject("p"));

        Assert.False(presenter.CanCommentOn("t1"));
        Assert.False(presenter.CanDrag);
        Assert.False(presenter.CanDragInSidebar(new SidebarNode(SidebarKind.Project, "p", "Work", 0, "project:p")));

        presenter.Reconnect("new", "u1");

        Assert.True(presenter.CanCommentOn("t1"));
        Assert.True(presenter.CanDrag);
    }

    [Fact]
    public async Task A_description_is_shown_but_not_written_while_the_token_is_turned_away()
    {
        var (presenter, _, _) = await TurnedAway();

        Assert.Equal(DescriptionAccess.ReadOnly, presenter.AccessToDescriptionOf(SubjectKind.Task, "t1"));
        Assert.Equal(DescriptionAccess.ReadOnly, presenter.AccessToDescriptionOf(SubjectKind.Project, "p"));

        presenter.Reconnect("new", "u1");

        Assert.Equal(DescriptionAccess.Writable, presenter.AccessToDescriptionOf(SubjectKind.Task, "t1"));
    }

    [Fact]
    public void The_commands_greyed_while_the_token_is_turned_away_are_the_ones_that_change_the_account()
    {
        // Spelt out rather than derived, so a command added without deciding which side it's on
        // fails here rather than staying lit over an account that can't take it.
        Assert.Equal(
            [
                AppCommand.ToggleComplete,
                AppCommand.Rename,
                AppCommand.NewSubtask,
                AppCommand.Due,
                AppCommand.Deadline,
                AppCommand.Priority1,
                AppCommand.Priority2,
                AppCommand.Priority3,
                AppCommand.Priority4,
                AppCommand.Labels,
                AppCommand.Reminders,
                AppCommand.Indent,
                AppCommand.Outdent,
                AppCommand.MoveUp,
                AppCommand.MoveDown,
                AppCommand.MoveTo,
                AppCommand.Delete,
                AppCommand.RenameSelection,
                AppCommand.DeleteSelection,
                AppCommand.ToggleFavourite,
                AppCommand.MoveSelectionUp,
                AppCommand.MoveSelectionDown,
                AppCommand.NewTask,
                AppCommand.NewProject,
                AppCommand.NewSection,
                AppCommand.QuickAdd,
            ],
            Enum.GetValues<AppCommand>().Where(Commands.IsWrite).ToArray());
    }

    [Fact]
    public void Every_write_is_greyed_while_the_token_is_turned_away_and_the_rest_are_left_alone()
    {
        var writable = Everything();
        var turnedAway = writable with { ReconnectNeeded = true };

        foreach (var command in Enum.GetValues<AppCommand>())
        {
            var before = Commands.StateOf(command, writable);
            var after = Commands.StateOf(command, turnedAway);

            if (Commands.IsWrite(command))
                Assert.False(after.Enabled, $"{command} is still lit");
            else if (command is not AppCommand.Reconnect)
                Assert.Equal(before.Enabled, after.Enabled);
        }

        Assert.False(Commands.StateOf(AppCommand.Reconnect, writable).Enabled);
        Assert.True(Commands.StateOf(AppCommand.Reconnect, turnedAway).Enabled);
    }

    /// <summary>A context in which as much as possible is on offer.</summary>
    private static CommandContext Everything() => new(
        Task: new TaskRow("t1", "Plan the week", Priority.P4, "Work", string.Empty, []),
        Abilities: new TaskAbilities(true, true, true, true, true, true),
        Selection: new SidebarNode(SidebarKind.Project, "p", "Work", 0, "project:p"),
        SelectionAbilities: new TaskAbilities(CanMoveUp: true, CanMoveDown: true),
        CanUndo: true,
        NewTaskText: "#Work ");

    /// <summary>
    /// A presenter over account u1, after Todoist has turned its token away.
    /// </summary>
    /// <param name="edit">Leave one change queued from before the rejection</param>
    private static async Task<(MainPresenter Presenter, FakeApi Api, FakeSecrets Secrets)> TurnedAway(bool edit = false)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource(ResourceType.User, ResourceType.User, """{"id":"u1"}""");
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("items", "t1", """{"id":"t1","content":"Plan the week","project_id":"p","child_order":1}""");

        var api = new FakeApi();
        var secrets = new FakeSecrets { Stored = "tok" };
        var engine = new SyncEngine(api, store, secrets, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));

        if (edit)
            engine.UpdateItem("t1", new JsonObject { ["content"] = "Plan the fortnight" });

        api.Throw = new TodoistAuthException("rejected");
        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.SyncAsync());
        api.Throw = null;

        return (presenter, api, secrets);
    }
}
