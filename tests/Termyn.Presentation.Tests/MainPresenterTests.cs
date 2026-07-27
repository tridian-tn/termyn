using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

public class MainPresenterTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public async Task Renders_active_tasks_ordered_by_child_order_with_project_names()
    {
        var api = new FakeApi
        {
            Response = new SyncResponse
            {
                SyncToken = "s1",
                Changes =
                [
                    Change("projects", "p1", """{"id":"p1","name":"Work"}"""),
                    Change("items", "done", """{"id":"done","content":"Done","checked":true,"child_order":0}"""),
                    Change("items", "b", """{"id":"b","content":"Second","project_id":"p1","priority":1,"child_order":2}"""),
                    Change("items", "a", """{"id":"a","content":"First","project_id":"pX","priority":4,"child_order":1}"""),
                ],
            },
        };
        var presenter = NewPresenter(api, new InMemorySnapshotStore());

        await presenter.LoadAsync();

        Assert.Equal(new[] { "First", "Second" }, presenter.Rows.Select(r => r.Content).ToArray());
        Assert.Equal(string.Empty, presenter.Rows.Single(r => r.Content == "First").Project); // unknown project id
        Assert.Equal("Work", presenter.Rows.Single(r => r.Content == "Second").Project);
        Assert.Equal(Priority.P1, presenter.Rows.Single(r => r.Content == "First").Priority); // API 4 -> P1
        Assert.False(presenter.IsOffline);
    }

    [Fact]
    public async Task Publishes_cached_rows_before_syncing()
    {
        var presenter = NewPresenter(new FakeApi { Response = new SyncResponse { SyncToken = "s1" } }, SeededStore());

        var publishes = new List<int>();
        presenter.RowsChanged += () => publishes.Add(presenter.Rows.Count);
        await presenter.LoadAsync();

        // Two publishes: the cached view first, then the reconciled one.
        Assert.Equal(2, publishes.Count);
        Assert.Equal(1, publishes[0]);
    }

    [Fact]
    public async Task Losing_the_network_keeps_the_cached_rows_and_reports_offline()
    {
        var presenter = NewPresenter(new FakeApi { Throw = new TodoistNetworkException("offline") }, SeededStore());

        await presenter.LoadAsync();

        Assert.Equal("Cached task", presenter.Rows.Single().Content);
        Assert.True(presenter.IsOffline);
        Assert.Contains("offline", presenter.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clears_token_and_rethrows_when_rejected()
    {
        var secrets = new FakeSecrets { Stored = "tok" };
        var engine = new SyncEngine(new FakeApi { Throw = new TodoistAuthException("no") }, new InMemorySnapshotStore(), secrets);
        var presenter = new MainPresenter(engine, Parser());

        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.LoadAsync());
        Assert.Null(secrets.Stored);
    }

    // ---- Capture -------------------------------------------------------------------------------

    [Fact]
    public async Task Capture_uses_the_server_parse_when_online()
    {
        var api = new FakeApi
        {
            QuickAdd = text => Change("items", "new", $$"""{"id":"new","content":"{{text}}"}"""),
        };
        var presenter = NewPresenter(api, new InMemorySnapshotStore());

        await presenter.CaptureAsync("Email report tomorrow p1");

        Assert.Equal(1, api.QuickAddCalls);
        Assert.Equal("Email report tomorrow p1", presenter.Rows.Single().Content);
        Assert.False(presenter.IsOffline);
    }

    [Fact]
    public async Task Capture_falls_back_to_the_local_grammar_when_offline()
    {
        var presenter = NewPresenter(new FakeApi(), new InMemorySnapshotStore()); // QuickAdd unset = unreachable

        await presenter.CaptureAsync("Email report tomorrow p1 @followup");

        var row = presenter.Rows.Single();
        Assert.Equal("Email report", row.Content);
        Assert.Equal(Priority.P1, row.Priority);
        Assert.Equal("2026-08-01", row.Due);
        Assert.True(presenter.IsOffline);
    }

    [Fact]
    public async Task Capture_ignores_blank_text()
    {
        var api = new FakeApi();
        var presenter = NewPresenter(api, new InMemorySnapshotStore());

        await presenter.CaptureAsync("   ");

        Assert.Empty(presenter.Rows);
        Assert.Equal(0, api.QuickAddCalls);
    }

    [Fact]
    public void Preview_reports_what_the_local_parser_understood()
    {
        var presenter = NewPresenter(new FakeApi(), new InMemorySnapshotStore());

        var preview = presenter.Preview("Water plants every day #Home");

        Assert.Equal("Home", preview.ProjectName);
        Assert.NotEmpty(preview.Unsupported); // recurrence is not handled offline
    }

    // ---- Intents -------------------------------------------------------------------------------

    [Fact]
    public void Completing_a_task_removes_it_from_the_list_and_can_be_undone()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Complete("i1");
        Assert.Empty(presenter.Rows);
        Assert.True(presenter.CanUndo);

        Assert.True(presenter.Undo());
        Assert.Equal("Cached task", presenter.Rows.Single().Content);
    }

    [Fact]
    public void Deleting_a_task_removes_it_and_can_be_undone()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Delete("i1");
        Assert.Empty(presenter.Rows);

        Assert.True(presenter.Undo());
        Assert.Equal("Cached task", presenter.Rows.Single().Content);
    }

    [Fact]
    public void Renaming_priority_and_due_date_show_up_immediately()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Rename("i1", "Renamed");
        presenter.SetPriority("i1", Priority.P2);
        presenter.SetDue("i1", Today);

        var row = presenter.Rows.Single();
        Assert.Equal("Renamed", row.Content);
        Assert.Equal(Priority.P2, row.Priority);
        Assert.Equal("2026-07-31", row.Due);
    }

    [Fact]
    public void Clearing_a_due_date_empties_the_column()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());
        presenter.SetDue("i1", Today);

        presenter.SetDue("i1", null);

        Assert.Equal(string.Empty, presenter.Rows.Single().Due);
    }

    [Fact]
    public void Search_filters_by_content_and_project()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("items", "a", """{"id":"a","content":"Email report","project_id":"p1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Buy milk","child_order":2}""");
        var presenter = NewPresenter(new FakeApi(), store);

        presenter.Search("milk");
        Assert.Equal("Buy milk", presenter.Rows.Single().Content);

        presenter.Search("work");
        Assert.Equal("Email report", presenter.Rows.Single().Content);

        presenter.Search("");
        Assert.Equal(2, presenter.Rows.Count);
    }

    [Fact]
    public void Moving_a_task_reorders_the_list()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","child_order":2}""");
        var presenter = NewPresenter(new FakeApi(), store);

        presenter.Move("b", -1);

        Assert.Equal(new[] { "B", "A" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    [Fact]
    public void Moving_past_either_end_does_nothing()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Move("i1", -1);
        presenter.Move("i1", 1);

        Assert.Equal("Cached task", presenter.Rows.Single().Content);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static MainPresenter NewPresenter(FakeApi api, InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return new MainPresenter(engine, Parser());
    }

    private static QuickAddParser Parser() => new(new FixedClock(Today));

    private static InMemorySnapshotStore SeededStore()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Cached task"}""");
        return store;
    }

    private static ResourceChange Change(string type, string id, string json)
        => new(type, id, false, (JsonObject)JsonNode.Parse(json)!);

    private sealed class FixedClock : IClock
    {
        private readonly DateOnly _today;

        public FixedClock(DateOnly today) => _today = today;

        public DateTimeOffset Now => new(_today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }
}
