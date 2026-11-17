using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// A token that's stored but can't be read just now: another program has its file open, or the
/// Keychain is locked. The account's still signed in, and a sync waits for the token rather than
/// failing over it.
/// </summary>
/// <remarks>
/// Here rather than in each window, which both kept a copy of this and had drifted apart: the Mac's
/// waited for any token it couldn't get, a deleted one included.
/// </remarks>
public class TokenWaitTests
{
    [Fact]
    public async Task A_token_out_of_reach_is_waited_for_and_tried_again_soon_rather_than_reported()
    {
        var (presenter, _, secrets, log) = SignedIn();
        await presenter.CaptureAsync("Buy milk", TestContext.Current.CancellationToken);
        secrets.Unreachable = true;

        var outcome = await presenter.SyncAsync(TestContext.Current.CancellationToken);
        await presenter.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncState.WaitingForToken, presenter.SyncStatus.State);
        Assert.Contains("Waiting to read the saved Todoist token… · 1 pending", presenter.Status);

        // Back soon, whatever the cadence, but not straight back: the write that's due can't go
        // until the token can be read, and asking for another round at once would spin.
        Assert.Equal(MainPresenter.TokenRetry, outcome.RetryIn);
        Assert.False(outcome.MoreQueued);

        // Once for the wait, not once a round.
        Assert.Single(log.Lines, l => l.Contains("can't be read yet", StringComparison.Ordinal));

        secrets.Unreachable = false;
        outcome = await presenter.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncState.Synced, presenter.SyncStatus.State);
        Assert.Null(outcome.RetryIn);
        Assert.Single(log.Lines, l => l.Contains("stopped waiting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_out_of_reach_at_startup_has_the_loop_try_again_soon()
    {
        // The windows start the loop from what loading says. When it said nothing, the loop started
        // on its ordinary interval, and in manual mode never came back at all.
        var (presenter, _, secrets, _) = SignedIn();
        secrets.Unreachable = true;

        var outcome = await presenter.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncState.WaitingForToken, presenter.SyncStatus.State);
        Assert.Equal(MainPresenter.TokenRetry, outcome.RetryIn);
    }

    [Fact]
    public async Task A_token_rejected_once_it_can_be_read_asks_to_reconnect_and_leaves_the_wait_behind()
    {
        var (presenter, api, secrets, _) = SignedIn();
        secrets.Unreachable = true;
        await presenter.SyncAsync(TestContext.Current.CancellationToken);

        secrets.Unreachable = false;
        api.Throw = new TodoistAuthException("rejected");
        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SyncState.ReconnectNeeded, presenter.SyncStatus.State);

        // And a new token doesn't bring back a wait that ended when the old one was sent.
        api.Throw = null;
        presenter.Reconnect("tok2", "u1");
        Assert.NotEqual(SyncState.WaitingForToken, presenter.SyncStatus.State);
    }

    [Fact]
    public async Task A_token_gone_altogether_isnt_waited_for()
    {
        // Nothing will hand it back, so waiting would be waiting for ever while saying all was well.
        var (presenter, _, secrets, _) = SignedIn();
        secrets.Stored = null;

        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.SyncAsync(TestContext.Current.CancellationToken));

        Assert.NotEqual(SyncState.WaitingForToken, presenter.SyncStatus.State);
    }

    [Fact]
    public async Task Showing_completed_tasks_while_the_token_is_out_of_reach_says_why_it_didnt()
    {
        // Something the user asked for, so it's reported rather than waited out like a sync.
        var (presenter, _, secrets, _) = SignedIn();
        secrets.Unreachable = true;

        var thrown = await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.ToggleCompletedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("The saved Todoist token can't be read just now. Try again in a moment.", thrown.Message);
        Assert.False(presenter.ShowingCompleted);
    }

    /// <summary>Account u1, signed in, holding one project.</summary>
    private static (MainPresenter Presenter, FakeApi Api, FakeSecrets Secrets, RecordingLog Log) SignedIn()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource(ResourceType.User, ResourceType.User, """{"id":"u1"}""");
        store.PutResource("projects", "p", """{"id":"p","name":"Inbox","inbox_project":true,"child_order":1}""");

        var api = new FakeApi();
        var secrets = new FakeSecrets { Stored = "tok" };
        var engine = new SyncEngine(api, store, secrets);
        engine.Load();

        var log = new RecordingLog();
        var clock = new SystemClock();
        return (new MainPresenter(engine, new QuickAddParser(clock), clock, log: log), api, secrets, log);
    }
}
