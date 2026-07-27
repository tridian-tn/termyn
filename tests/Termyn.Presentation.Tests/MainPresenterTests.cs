using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

public class MainPresenterTests
{
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
        var presenter = new MainPresenter(NewEngine(api, new InMemorySnapshotStore()));

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
        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var presenter = new MainPresenter(NewEngine(api, SeededStore()));

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
        var api = new FakeApi { Throw = new TodoistNetworkException("offline") };
        var presenter = new MainPresenter(NewEngine(api, SeededStore()));

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
        var presenter = new MainPresenter(engine);

        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.LoadAsync());
        Assert.Null(secrets.Stored);
    }

    private static SyncEngine NewEngine(FakeApi api, InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    private static InMemorySnapshotStore SeededStore()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Cached task"}""");
        return store;
    }

    private static ResourceChange Change(string type, string id, string json)
        => new(type, id, false, (JsonObject)JsonNode.Parse(json)!);
}
