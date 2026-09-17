using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.History;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Signing out, and what goes with the account when it does.
/// </summary>
/// <remarks>
/// The engine's half — the token, the cache, the queue — is tested with the engine. This is the
/// half the presenter holds: the history, which quotes the account's tasks by name, and the question
/// asked before any of it goes.
/// </remarks>
public class SignOutTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>A history store held in a list, so what's left in it can be read back.</summary>
    private sealed class Held : IHistoryStore
    {
        public List<StoredAction> Entries { get; } = [];

        public void Append(IReadOnlyList<StoredAction> entries) => Entries.InsertRange(0, entries.Reverse());

        public IReadOnlyList<StoredAction> Recent(int limit) => Entries.Take(limit).ToList();

        public int Sweep(DateTimeOffset before) => Entries.RemoveAll(e => e.At < before);

        public void Clear() => Entries.Clear();

        public void Dispose() { }
    }

    private static (MainPresenter Presenter, SyncEngine Engine, FakeApi Api, Held History) Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("items", "t1", """{"id":"t1","content":"Plan the week","project_id":"p","child_order":1}""");
        store.PutResource("items", "t2", """{"id":"t2","content":"Tidy the garage","project_id":"p","child_order":2}""");

        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var history = new Held();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today), history: history);
        return (presenter, engine, api, history);
    }

    /// <summary>Leaves one line of history written to the store and another still in memory.</summary>
    private static void DoSomeWork(MainPresenter presenter)
    {
        presenter.SetDue("t1", null);
        presenter.SetDue("t2", null);
        presenter.History.Flush();
        presenter.SetDue("t1", new DateOnly(2026, 8, 15));
    }

    [Fact]
    public void Signing_out_forgets_the_account_and_what_was_done_to_it()
    {
        var (presenter, _, _, history) = Seeded();
        DoSomeWork(presenter);
        Assert.NotEmpty(history.Entries);

        presenter.SignOut();

        Assert.Empty(presenter.Rows);
        Assert.Empty(presenter.History.Recent());
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task A_rejected_token_takes_what_was_done_with_it()
    {
        // The engine already wiped the tasks and the downloaded files here, so that a different
        // account signing in next isn't shown the last one's. The history quotes those same tasks
        // by name and was left behind.
        var (presenter, _, api, history) = Seeded();
        DoSomeWork(presenter);
        api.Throw = new TodoistAuthException("rejected");

        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.SyncAsync());

        Assert.Empty(presenter.History.Recent());
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void With_everything_sent_the_question_mentions_no_loss()
    {
        var (presenter, _, _, _) = Seeded();

        var question = presenter.SignOutQuestion();

        Assert.StartsWith("Sign out of Todoist?", question);
        Assert.Contains("Nothing is removed from Todoist itself.", question);
        Assert.DoesNotContain("hasn't reached", question);
        Assert.DoesNotContain("haven't reached", question);
    }

    [Fact]
    public void One_change_still_to_send_is_counted_in_the_question()
    {
        var (presenter, engine, _, _) = Seeded();
        engine.UpdateItem("t1", new JsonObject { ["content"] = "Plan the month" });

        Assert.EndsWith(
            "1 change hasn't reached Todoist yet, and signing out now loses it. Sync first if you want to keep it.",
            presenter.SignOutQuestion());
    }

    [Fact]
    public void Several_changes_still_to_send_are_counted_in_the_question()
    {
        var (presenter, engine, _, _) = Seeded();
        engine.UpdateItem("t1", new JsonObject { ["content"] = "Plan the month" });
        engine.UpdateItem("t2", new JsonObject { ["content"] = "Tidy the shed" });

        Assert.EndsWith(
            "2 changes haven't reached Todoist yet, and signing out now loses them. Sync first if you want to keep them.",
            presenter.SignOutQuestion());
    }
}
