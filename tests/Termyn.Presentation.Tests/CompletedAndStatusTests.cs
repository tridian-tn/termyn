using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

public class CompletedViewTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public async Task Completed_tasks_are_hidden_until_they_are_asked_for()
    {
        var presenter = await Loaded(WithCompleted(Done("c1", "Book dentist")));

        Assert.False(presenter.ShowingCompleted);
        Assert.DoesNotContain(presenter.Rows, r => r.Id == "c1");
    }

    [Fact]
    public async Task Toggling_fetches_them_and_marks_them_as_done()
    {
        var api = WithCompleted(Done("c1", "Book dentist"));
        var presenter = await Loaded(api);

        Assert.True(await presenter.ToggleCompletedAsync());

        Assert.True(presenter.ShowingCompleted);
        var row = Assert.Single(presenter.Rows, r => r.Id == "c1");
        Assert.True(row.Completed);
        Assert.Single(api.CompletedQueries);
    }

    [Fact]
    public async Task Toggling_off_drops_them_without_another_round_trip()
    {
        var api = WithCompleted(Done("c1", "Book dentist"));
        var presenter = await Loaded(api);
        await presenter.ToggleCompletedAsync();

        Assert.True(await presenter.ToggleCompletedAsync());

        Assert.False(presenter.ShowingCompleted);
        Assert.DoesNotContain(presenter.Rows, r => r.Id == "c1");
        Assert.Single(api.CompletedQueries);
    }

    [Fact]
    public async Task Completed_tasks_come_after_the_active_ones_most_recent_first()
    {
        var api = WithCompleted(
            Done("old", "Older", "2026-07-01T09:00:00Z"),
            Done("new", "Newer", "2026-07-29T09:00:00Z"));
        api.Response = new SyncResponse
        {
            SyncToken = "s1",
            Changes = [Json.Change("items", "a", """{"id":"a","content":"Active"}""")],
        };
        var presenter = await Loaded(api);

        await presenter.ToggleCompletedAsync();

        Assert.Equal(["a", "new", "old"], presenter.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task A_completed_task_is_only_shown_in_the_view_it_belongs_to()
    {
        var api = WithCompleted(
            Done("cw", "Work one", project: "p1"),
            Done("ch", "Home one", project: "p2"));
        api.Response = new SyncResponse
        {
            SyncToken = "s1",
            Changes =
            [
                Json.Change("projects", "p1", """{"id":"p1","name":"Work"}"""),
                Json.Change("projects", "p2", """{"id":"p2","name":"Home"}"""),
            ],
        };
        var presenter = await Loaded(api);
        await presenter.ToggleCompletedAsync();

        presenter.Select(ViewSelection.OfProject("p1"));

        // One account-wide fetch serves every view; which of them shows is decided locally.
        Assert.Equal(["cw"], presenter.Rows.Select(r => r.Id).ToArray());
        Assert.Single(api.CompletedQueries);
    }

    [Fact]
    public async Task A_completed_task_reaches_a_label_view_too()
    {
        var api = WithCompleted(Json.Change("items", "c1", """{"id":"c1","content":"Done","checked":true,"labels":["urgent"]}"""));
        api.Response = new SyncResponse
        {
            SyncToken = "s1",
            Changes = [Json.Change("labels", "l1", """{"id":"l1","name":"urgent"}""")],
        };
        var presenter = await Loaded(api);
        await presenter.ToggleCompletedAsync();

        presenter.Select(ViewSelection.OfLabel("urgent"));

        Assert.Equal(["c1"], presenter.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Reopening_moves_it_back_among_the_active_tasks()
    {
        var presenter = await Loaded(WithCompleted(Done("c1", "Book dentist")));
        await presenter.ToggleCompletedAsync();

        presenter.Reopen("c1");

        var row = Assert.Single(presenter.Rows, r => r.Id == "c1");
        Assert.False(row.Completed);
    }

    [Fact]
    public async Task A_fetch_that_cannot_be_made_leaves_the_toggle_off()
    {
        var api = new FakeApi
        {
            Response = new SyncResponse { SyncToken = "s1" },
            CompletedThrow = new TodoistNetworkException("offline"),
        };
        var presenter = await Loaded(api);

        Assert.False(await presenter.ToggleCompletedAsync());

        // Switched on and empty would read as "you have completed nothing".
        Assert.False(presenter.ShowingCompleted);
        Assert.True(presenter.IsOffline);
    }

    [Fact]
    public async Task A_truncated_history_says_so_in_the_status_line()
    {
        var api = new FakeApi
        {
            Response = new SyncResponse { SyncToken = "s1" },
            Completed = _ => new CompletedPage([Done("c1", "One")], "more"),
        };
        var presenter = await Loaded(api);

        await presenter.ToggleCompletedAsync();

        Assert.True(presenter.CompletedTruncated);
        Assert.Contains("most recent completed only", presenter.Status);
    }

    [Fact]
    public async Task Turning_it_off_takes_the_truncation_notice_with_it()
    {
        var api = new FakeApi
        {
            Response = new SyncResponse { SyncToken = "s1" },
            Completed = _ => new CompletedPage([Done("c1", "One")], "more"),
        };
        var presenter = await Loaded(api);
        await presenter.ToggleCompletedAsync();

        await presenter.ToggleCompletedAsync();

        Assert.False(presenter.CompletedTruncated);
        Assert.DoesNotContain("most recent", presenter.Status);
    }

    [Fact]
    public async Task Searching_reaches_completed_tasks_once_they_are_loaded()
    {
        var presenter = await Loaded(WithCompleted(Done("c1", "Book dentist")));
        await presenter.ToggleCompletedAsync();

        presenter.Search("dentist");

        Assert.Equal(["c1"], presenter.Rows.Select(r => r.Id).ToArray());
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static async Task<MainPresenter> Loaded(FakeApi api)
    {
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
        presenter.Select(ViewSelection.Of(SmartView.All));
        await presenter.LoadAsync();
        return presenter;
    }

    private static FakeApi WithCompleted(params ResourceChange[] items) => new()
    {
        Response = new SyncResponse { SyncToken = "s1" },
        Completed = _ => new CompletedPage(items, null),
    };

    private static ResourceChange Done(string id, string content, string? at = null, string? project = null)
    {
        var fields = new List<string> { $"\"id\":\"{id}\"", $"\"content\":\"{content}\"", "\"checked\":true" };
        if (at is not null)
            fields.Add($"\"completed_at\":\"{at}\"");
        if (project is not null)
            fields.Add($"\"project_id\":\"{project}\"");
        return Json.Change("items", id, "{" + string.Join(",", fields) + "}");
    }
}

public class SyncStatusTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public void Nothing_synced_yet_says_so()
        => Assert.Equal("Not synced yet", new SyncStatus(SyncState.Never).Describe());

    [Fact]
    public void A_recent_sync_reads_in_the_coarsest_unit_that_says_something()
    {
        Assert.Equal("just now", SyncStatus.Ago(TimeSpan.FromSeconds(2)));
        Assert.Equal("12s ago", SyncStatus.Ago(TimeSpan.FromSeconds(12)));
        Assert.Equal("3m ago", SyncStatus.Ago(TimeSpan.FromMinutes(3.5)));
        Assert.Equal("2h ago", SyncStatus.Ago(TimeSpan.FromHours(2.5)));
        Assert.Equal("4d ago", SyncStatus.Ago(TimeSpan.FromDays(4.2)));
    }

    [Fact]
    public void A_clock_that_stepped_backwards_does_not_produce_a_negative_age()
        => Assert.Equal("just now", SyncStatus.Ago(TimeSpan.FromSeconds(-30)));

    [Fact]
    public void A_pause_counts_down_in_whole_seconds()
    {
        Assert.Equal("Paused (retry in 42s)", new SyncStatus(SyncState.Paused, RetryIn: TimeSpan.FromSeconds(42)).Describe());

        // Under a second is still a wait, so it rounds up rather than down to none.
        Assert.Equal("Paused (retry in 1s)", new SyncStatus(SyncState.Paused, RetryIn: TimeSpan.FromMilliseconds(200)).Describe());
    }

    [Fact]
    public void Outstanding_work_is_appended_to_whatever_the_state_is()
    {
        var status = new SyncStatus(SyncState.Offline, Pending: 3, Failed: 1);

        Assert.Equal("Offline (showing cached) · 3 pending · 1 failed", status.Describe());
    }

    [Fact]
    public async Task A_successful_sync_reports_how_long_ago_it_was()
    {
        var clock = new SteppableClock(Today);
        var presenter = Presenter(new FakeApi { Response = new SyncResponse { SyncToken = "s1" } }, clock);

        await presenter.SyncAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        presenter.PublishStatus();

        Assert.Equal(SyncState.Synced, presenter.SyncStatus.State);
        Assert.Contains("Synced 20s ago", presenter.Status);
    }

    [Fact]
    public async Task A_sync_in_flight_says_so_and_then_stops_saying_it()
    {
        var clock = new SteppableClock(Today);
        var seen = new List<SyncState>();
        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var presenter = Presenter(api, clock);
        presenter.StatusChanged += () => seen.Add(presenter.SyncStatus.State);

        await presenter.SyncAsync();

        Assert.Equal([SyncState.Syncing], seen);
        Assert.Equal(SyncState.Synced, presenter.SyncStatus.State);
    }

    [Fact]
    public void A_status_only_refresh_leaves_the_rows_alone()
    {
        var clock = new SteppableClock(Today);
        var presenter = Presenter(new FakeApi { Response = new SyncResponse { SyncToken = "s1" } }, clock);
        var rowPublishes = 0;
        presenter.RowsChanged += () => rowPublishes++;

        presenter.PublishStatus();

        // Repainting a five-thousand-row outline to change one word is what this exists to avoid.
        Assert.Equal(0, rowPublishes);
    }

    [Fact]
    public async Task A_rate_limit_pauses_for_as_long_as_the_server_asked()
    {
        var clock = new SteppableClock(Today);
        var api = new FakeApi { Throw = new TodoistRateLimitException("slow down", TimeSpan.FromSeconds(30)) };
        var presenter = Presenter(api, clock);

        var outcome = await presenter.SyncAsync();

        Assert.Equal(TimeSpan.FromSeconds(30), outcome.PauseFor);
        Assert.False(outcome.MoreQueued);
        Assert.Equal(SyncState.Paused, presenter.SyncStatus.State);
        Assert.False(presenter.IsOffline); // being refused is not being unreachable
        Assert.Contains("Paused (retry in 30s)", presenter.Status);
    }

    [Fact]
    public async Task A_rate_limit_with_no_advice_backs_off_and_keeps_backing_off()
    {
        var clock = new SteppableClock(Today);
        var api = new FakeApi { Throw = new TodoistRateLimitException("slow down", null) };
        var presenter = Presenter(api, clock);

        var first = (await presenter.SyncAsync()).PauseFor!.Value;
        var second = (await presenter.SyncAsync()).PauseFor!.Value;

        Assert.True(first >= TimeSpan.FromSeconds(2), $"first wait was {first}");
        Assert.True(second > first, $"{second} should be longer than {first}");
        Assert.True(second <= TimeSpan.FromSeconds(375), $"{second} is beyond the ceiling"); // 300s + 25% jitter
    }

    [Fact]
    public async Task A_sync_that_succeeds_clears_the_pause_and_the_backoff()
    {
        var clock = new SteppableClock(Today);
        var api = new FakeApi { Throw = new TodoistRateLimitException("slow down", null) };
        var presenter = Presenter(api, clock);
        await presenter.SyncAsync();
        await presenter.SyncAsync();

        api.Throw = null;
        api.Response = new SyncResponse { SyncToken = "s1" };
        Assert.Null((await presenter.SyncAsync()).PauseFor);

        // Back to the first step, not the third, so one bad patch doesn't punish the next one.
        api.Throw = new TodoistRateLimitException("slow down", null);
        Assert.True((await presenter.SyncAsync()).PauseFor < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_rejected_token_says_reconnect_and_keeps_saying_it()
    {
        var clock = new SteppableClock(Today);
        var api = new FakeApi { Throw = new TodoistAuthException("rejected") };
        var presenter = Presenter(api, clock);

        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.SyncAsync());

        Assert.Equal(SyncState.ReconnectNeeded, presenter.SyncStatus.State);
        Assert.Contains("Reconnect needed", presenter.Status);
    }

    [Fact]
    public async Task Losing_the_network_reads_as_offline_rather_than_stale()
    {
        var clock = new SteppableClock(Today);
        var presenter = Presenter(new FakeApi { Throw = new TodoistNetworkException("offline") }, clock);

        await presenter.SyncAsync();

        Assert.Equal(SyncState.Offline, presenter.SyncStatus.State);
    }

    private static MainPresenter Presenter(FakeApi api, IClock clock)
    {
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" }, clock);
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(clock), clock);
    }

    /// <summary>A clock that only moves when a test moves it, so "12s ago" is a fact rather than a race.</summary>
    private sealed class SteppableClock : IClock
    {
        private DateTimeOffset _now;

        public SteppableClock(DateOnly today) => _now = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

        public DateOnly Today => DateOnly.FromDateTime(_now.UtcDateTime);

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
