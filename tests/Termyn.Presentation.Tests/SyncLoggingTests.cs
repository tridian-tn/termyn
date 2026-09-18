using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// What the presenter writes down about a sync that didn't go through.
/// </summary>
/// <remarks>
/// The status bar says what is happening now; the log is for reading afterwards, when the window
/// has been shut for a week. So what matters here is that it records the change rather than the
/// state: a laptop left offline overnight syncs nine hundred times, and nine hundred identical
/// lines would bury the one that says what happened.
/// </remarks>
public class SyncLoggingTests
{
    private static readonly DateOnly Today = new(2026, 9, 18);

    private static (MainPresenter Presenter, FakeApi Api, RecordingLog Log) Built()
    {
        var api = new FakeApi();
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var log = new RecordingLog();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today), log: log);
        return (presenter, api, log);
    }

    [Fact]
    public async Task Going_offline_is_written_down_once_however_long_it_lasts()
    {
        var (presenter, api, log) = Built();
        api.Throw = new TodoistNetworkException("the network is not there");

        await presenter.SyncAsync();
        await presenter.SyncAsync();
        await presenter.SyncAsync();

        Assert.Single(log.Lines);
        Assert.Contains("Todoist couldn't be reached", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coming_back_is_written_down_too()
    {
        var (presenter, api, log) = Built();
        api.Throw = new TodoistNetworkException("the network is not there");
        await presenter.SyncAsync();

        api.Throw = null;
        await presenter.SyncAsync();

        Assert.Contains("Todoist can be reached again", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Being_rate_limited_says_how_long_the_wait_is()
    {
        var (presenter, api, log) = Built();
        api.Throw = new TodoistRateLimitException("too many", TimeSpan.FromSeconds(30));

        await presenter.SyncAsync();

        Assert.Contains("rate-limiting us; waiting 30s", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sync_that_works_leaves_nothing_behind()
    {
        var (presenter, _, log) = Built();

        await presenter.SyncAsync();

        Assert.Empty(log.Lines);
    }
}
