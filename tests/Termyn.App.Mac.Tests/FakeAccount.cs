using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Mac.Tests;

/// <summary>A presenter on an account held in memory, showing every task it has.</summary>
/// <remarks>
/// A task with nothing above it and no date (<c>a</c>), one under it with a date (<c>b</c>), and two
/// labels. <c>a</c> also wears one the account doesn't list any more, as one left behind by a rename
/// is. Only the fakes are given to the engine, so nothing here can reach Todoist, the Keychain or
/// the app's folders.
/// </remarks>
internal static class FakeAccount
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <param name="api">The server to answer the presenter, for a test that means to sync</param>
    /// <param name="plan">The plan the account is on, as a sync would say it, or null for none said yet</param>
    /// <param name="prepare">Anything more the account should hold</param>
    public static MainPresenter Presenter(FakeApi? api = null, (string Name, bool Reminders)? plan = null, Action<InMemorySnapshotStore>? prepare = null)
    {
        var store = new InMemorySnapshotStore();
        if (plan is { } said)
            store.PutResource("user_plan_limits", "user_plan_limits", $$$"""{"current":{"plan_name":"{{{said.Name}}}","reminders":{{{(said.Reminders ? "true" : "false")}}}}}""");

        prepare?.Invoke(store);
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"urgent","item_order":2}""");
        store.PutResource("items", "a", """{"id":"a","content":"Ship it","project_id":"p1","child_order":1,"priority":4,"labels":["home","gone"]}""");
        store.PutResource("items", "b", """{"id":"b","content":"Review it","project_id":"p1","child_order":2,"due":{"date":"2026-08-04","string":"4 Aug","is_recurring":false}}""");

        var clock = new FixedClock(Today);
        var engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" }, clock);
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }

    /// <summary>The commands over a presenter, with nowhere for anything they say to go.</summary>
    public static TaskCommands Commands(MainPresenter presenter) => new(presenter, new RecordingLog(), _ => { });
}
