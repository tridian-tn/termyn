using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

public class CompletedItemsTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public async Task Fetched_completed_tasks_show_up_in_the_snapshot()
    {
        var api = Returning([Done("c1", "Book dentist", "2026-07-30T09:00:00Z")]);
        var engine = NewEngine(api, new InMemorySnapshotStore());

        var fetch = await engine.FetchCompletedAsync();

        Assert.Equal(1, fetch.Count);
        Assert.False(fetch.Truncated);
        var completed = Assert.Single(engine.Snapshot().CompletedItems);
        Assert.Equal("Book dentist", completed.Content);
        Assert.Equal("2026-07-30T09:00:00Z", completed.CompletedAt);
    }

    [Fact]
    public async Task The_window_asked_for_is_the_three_months_the_endpoint_allows()
    {
        var api = Returning([]);
        var engine = NewEngine(api, new InMemorySnapshotStore());

        await engine.FetchCompletedAsync();

        var query = Assert.Single(api.CompletedQueries);
        Assert.Equal(CompletedQuery.MaxWindow, query.Until - query.Since);
        Assert.Null(query.Cursor);
    }

    [Fact]
    public async Task Paging_follows_the_cursor_until_the_server_stops_offering_one()
    {
        var api = new FakeApi
        {
            Completed = q => q.Cursor switch
            {
                null => new CompletedPage([Done("c1", "One")], "page2"),
                "page2" => new CompletedPage([Done("c2", "Two")], "page3"),
                _ => new CompletedPage([Done("c3", "Three")], null),
            },
        };
        var engine = NewEngine(api, new InMemorySnapshotStore());

        var fetch = await engine.FetchCompletedAsync();

        Assert.Equal(3, fetch.Count);
        Assert.False(fetch.Truncated);
        Assert.Equal(["", "page2", "page3"], api.CompletedQueries.Select(q => q.Cursor ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task An_endless_history_stops_at_the_page_ceiling_and_says_so()
    {
        // Every page offers another, so nothing but the ceiling can end this.
        var page = 0;
        var api = new FakeApi { Completed = _ => new CompletedPage([Done("c" + page++, "Task")], "more") };
        var engine = NewEngine(api, new InMemorySnapshotStore());

        var fetch = await engine.FetchCompletedAsync();

        Assert.True(fetch.Truncated);
        Assert.Equal(10, api.CompletedQueries.Count);
    }

    [Fact]
    public async Task Completed_tasks_are_never_written_to_the_snapshot()
    {
        var store = new InMemorySnapshotStore();
        var engine = NewEngine(Returning([Done("c1", "Book dentist")]), store);

        await engine.FetchCompletedAsync();

        // Nothing durable: incremental sync never mentions completed tasks, so a persisted copy
        // would have nothing to correct it and no tombstone to remove it.
        Assert.Empty(store.Load().Resources);
    }

    [Fact]
    public async Task A_reloaded_engine_has_forgotten_them()
    {
        var store = new InMemorySnapshotStore();
        var engine = NewEngine(Returning([Done("c1", "Book dentist")]), store);
        await engine.FetchCompletedAsync();

        engine.Load();

        Assert.Empty(engine.Snapshot().CompletedItems);
    }

    [Fact]
    public async Task Clearing_drops_the_fetch_but_keeps_what_the_model_holds()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "local", """{"id":"local","content":"Ticked here","checked":true}""");
        var engine = NewEngine(Returning([Done("c1", "From the server")]), store);
        engine.Load();
        await engine.FetchCompletedAsync();
        Assert.Equal(2, engine.Snapshot().CompletedItems.Count);

        engine.ClearCompleted();

        var kept = Assert.Single(engine.Snapshot().CompletedItems);
        Assert.Equal("Ticked here", kept.Content);
    }

    [Fact]
    public async Task The_model_wins_where_both_have_a_copy()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "c1", """{"id":"c1","content":"Edited here","checked":true}""");
        var engine = NewEngine(Returning([Done("c1", "Stale server copy")]), store);
        engine.Load();

        await engine.FetchCompletedAsync();

        var completed = Assert.Single(engine.Snapshot().CompletedItems);
        Assert.Equal("Edited here", completed.Content);
    }

    [Fact]
    public async Task Reopening_a_fetched_task_moves_it_among_the_active_ones()
    {
        var store = new InMemorySnapshotStore();
        var engine = NewEngine(Returning([Done("c1", "Book dentist")]), store);
        await engine.FetchCompletedAsync();

        engine.ReopenItem("c1");

        Assert.Empty(engine.Snapshot().CompletedItems);
        var active = Assert.Single(engine.Snapshot().Items);
        Assert.Equal("Book dentist", active.Content);
        Assert.False(active.Completed);

        // It is an ordinary task now, so it belongs in the snapshot: from here on incremental sync
        // is what keeps it current.
        Assert.Equal("items", Assert.Single(store.Load().Resources).Type);

        var command = Assert.Single(engine.Outbox);
        Assert.Equal("item_uncomplete", command.Type);
    }

    [Fact]
    public async Task Reopening_a_fetched_task_records_no_prior_state()
    {
        // A prior would have undo write a completed task back into the snapshot, where nothing would
        // ever arrive to remove it again.
        var engine = NewEngine(Returning([Done("c1", "Book dentist")]), new InMemorySnapshotStore());
        await engine.FetchCompletedAsync();

        engine.ReopenItem("c1");

        Assert.Null(Assert.Single(engine.Outbox).PriorJson);
    }

    [Fact]
    public async Task A_deleted_task_leaves_the_completed_list_when_its_tombstone_arrives()
    {
        var api = Returning([Done("c1", "Book dentist")]);
        var engine = NewEngine(api, new InMemorySnapshotStore());
        await engine.FetchCompletedAsync();
        Assert.Single(engine.Snapshot().CompletedItems);

        api.Response = new SyncResponse { SyncToken = "s1", Changes = [Json.Deleted("items", "c1")] };
        await engine.SyncAsync();

        Assert.Empty(engine.Snapshot().CompletedItems);
    }

    [Fact]
    public async Task A_rejected_token_takes_the_completed_list_with_the_rest()
    {
        var api = Returning([Done("c1", "Book dentist")]);
        var secrets = new FakeSecrets { Stored = "tok" };
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), secrets, new FixedClock(Today));
        await engine.FetchCompletedAsync();

        api.Throw = new TodoistAuthException("rejected");
        await Assert.ThrowsAsync<TodoistAuthException>(() => engine.SyncAsync());

        Assert.Empty(engine.Snapshot().CompletedItems);
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public async Task A_token_rejected_by_the_fetch_itself_clears_the_account_too()
    {
        var secrets = new FakeSecrets { Stored = "tok" };
        var api = new FakeApi { CompletedThrow = new TodoistAuthException("rejected") };
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Cached"}""");
        var engine = new SyncEngine(api, store, secrets, new FixedClock(Today));
        engine.Load();

        await Assert.ThrowsAsync<TodoistAuthException>(() => engine.FetchCompletedAsync());

        Assert.Null(secrets.Stored);
        Assert.Empty(engine.Snapshot().Items);
    }

    [Fact]
    public async Task A_fetch_that_lands_after_the_cache_was_wiped_is_dropped()
    {
        var secrets = new FakeSecrets { Stored = "tok" };
        var api = new FakeApi();
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), secrets, new FixedClock(Today));

        // The purge happens while the fetch is in flight, which is what the generation guard is for.
        api.Completed = _ =>
        {
            api.Throw = new TodoistAuthException("rejected");
            engine.SyncAsync().GetAwaiter().GetResult();
            return new CompletedPage([Done("c1", "From the old account")], null);
        };
        api.Response = new SyncResponse { SyncToken = "s1" };

        await Assert.ThrowsAsync<TodoistAuthException>(async () =>
        {
            var fetch = await engine.FetchCompletedAsync();
            Assert.Equal(0, fetch.Count);
        });

        Assert.Empty(engine.Snapshot().CompletedItems);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static SyncEngine NewEngine(FakeApi api, InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return engine;
    }

    private static FakeApi Returning(IReadOnlyList<ResourceChange> items)
        => new() { Completed = _ => new CompletedPage(items, null) };

    private static ResourceChange Done(string id, string content, string? at = null)
        => Json.Change("items", id, at is null
            ? $$"""{"id":"{{id}}","content":"{{content}}","checked":true}"""
            : $$"""{"id":"{{id}}","content":"{{content}}","checked":true,"completed_at":"{{at}}"}""");
}
