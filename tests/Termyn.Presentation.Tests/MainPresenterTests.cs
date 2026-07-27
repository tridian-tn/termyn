using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

public class MainPresenterTests
{
    [Fact]
    public async Task Excludes_completed_and_orders_by_child_order()
    {
        var api = new FakeApi
        {
            Result = Result(items:
            [
                Item("done", childOrder: 0, completed: true),
                Item("second", childOrder: 2),
                Item("first", childOrder: 1),
            ]),
        };
        var presenter = new MainPresenter(api, new FakeSecrets());

        await presenter.LoadAsync();

        Assert.Equal(new[] { "first", "second" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    [Fact]
    public async Task Joins_project_names_and_falls_back_to_empty_for_unknown_or_null()
    {
        var api = new FakeApi
        {
            Result = Result(
                items:
                [
                    Item("known", projectId: "p1"),
                    Item("unknown", projectId: "pX"),
                    Item("none", projectId: null),
                ],
                projects: [Proj("p1", "Work")]),
        };
        var presenter = new MainPresenter(api, new FakeSecrets());

        await presenter.LoadAsync();

        Assert.Equal("Work", presenter.Rows.Single(r => r.Content == "known").Project);
        Assert.Equal(string.Empty, presenter.Rows.Single(r => r.Content == "unknown").Project);
        Assert.Equal(string.Empty, presenter.Rows.Single(r => r.Content == "none").Project);
    }

    [Fact]
    public async Task Due_prefers_text_then_date_then_empty()
    {
        var api = new FakeApi
        {
            Result = Result(items:
            [
                Item("both", dueText: "Friday", dueDate: "2026-07-31"),
                Item("dateonly", dueDate: "2026-08-01"),
                Item("none"),
            ]),
        };
        var presenter = new MainPresenter(api, new FakeSecrets());

        await presenter.LoadAsync();

        Assert.Equal("Friday", presenter.Rows.Single(r => r.Content == "both").Due);
        Assert.Equal("2026-08-01", presenter.Rows.Single(r => r.Content == "dateonly").Due);
        Assert.Equal(string.Empty, presenter.Rows.Single(r => r.Content == "none").Due);
    }

    [Fact]
    public async Task Tolerates_duplicate_project_ids()
    {
        var api = new FakeApi
        {
            Result = Result(
                items: [Item("t", projectId: "dup")],
                projects: [Proj("dup", "First"), Proj("dup", "Second")]),
        };
        var presenter = new MainPresenter(api, new FakeSecrets());

        await presenter.LoadAsync(); // must not throw on duplicate keys

        Assert.Single(presenter.Rows);
    }

    [Fact]
    public async Task Throws_and_skips_api_when_no_token_stored()
    {
        var api = new FakeApi();
        var presenter = new MainPresenter(api, new FakeSecrets { Stored = null });

        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.LoadAsync());
        Assert.Equal(0, api.SyncCalls);
    }

    [Fact]
    public async Task Clears_token_and_rethrows_when_rejected()
    {
        var secrets = new FakeSecrets { Stored = "tok" };
        var api = new FakeApi { Throw = new TodoistAuthException("rejected") };
        var presenter = new MainPresenter(api, secrets);

        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.LoadAsync());
        Assert.Null(secrets.Stored);
    }

    private static SyncResult Result(IReadOnlyList<TaskItem>? items = null, IReadOnlyList<Project>? projects = null)
        => new() { SyncToken = "x", Items = items ?? [], Projects = projects ?? [] };

    private static TaskItem Item(string content, string? projectId = null, int childOrder = 0, bool completed = false, string? dueText = null, string? dueDate = null)
        => new()
        {
            Id = content,
            Content = content,
            ProjectId = projectId,
            ChildOrder = childOrder,
            Completed = completed,
            DueText = dueText,
            DueDate = dueDate,
        };

    private static Project Proj(string id, string name) => new() { Id = id, Name = name };

    private sealed class FakeSecrets : ISecretStore
    {
        public string? Stored = "tok";

        public string? GetToken() => Stored;
        public void SetToken(string token) => Stored = token;
        public void ClearToken() => Stored = null;
    }

    private sealed class FakeApi : ITodoistApi
    {
        public SyncResult Result = new() { SyncToken = "x" };
        public Exception? Throw;
        public int SyncCalls;

        public Task<SyncResult> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, CancellationToken ct = default)
        {
            SyncCalls++;
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(Result);
        }

        public Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
