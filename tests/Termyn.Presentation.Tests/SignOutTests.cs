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
        store.PutResource("user", "user", """{"id":"u1"}""");
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
    public async Task A_rejected_token_keeps_what_was_done_until_another_account_comes()
    {
        // The account is held for the next token, and its history with it. The history quotes the
        // account's tasks by name, so it goes when a different account's token wipes them.
        var (presenter, _, api, history) = Seeded();
        DoSomeWork(presenter);
        api.Throw = new TodoistAuthException("rejected");

        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.SyncAsync());

        Assert.NotEmpty(presenter.History.Recent());
        Assert.NotEmpty(history.Entries);

        presenter.Reconnect("other", "u2");

        Assert.Empty(presenter.History.Recent());
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void A_history_that_cannot_be_emptied_does_not_stop_a_switch_of_account()
    {
        // The history is cleared from inside the engine's purge, before the new token is stored.
        // If its file refused and that failure got out, the switch would stop with the cache gone
        // and no token, and the window would be left asking for one it had just been given.
        var store = new InMemorySnapshotStore();
        store.PutResource("user", "user", """{"id":"u1"}""");
        var secrets = new FakeSecrets { Stored = null };
        var engine = new SyncEngine(new FakeApi(), store, secrets, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today), history: new Locked());

        presenter.Reconnect("other", "u2");

        Assert.Equal("other", secrets.Stored);
        Assert.False(presenter.ReconnectNeeded);
    }

    /// <summary>A history store whose file can't be emptied.</summary>
    private sealed class Locked : IHistoryStore
    {
        public void Append(IReadOnlyList<StoredAction> entries) { }

        public IReadOnlyList<StoredAction> Recent(int limit) => [];

        public int Sweep(DateTimeOffset before) => 0;

        public void Clear() => throw new IOException("locked");

        public void Dispose() { }
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
