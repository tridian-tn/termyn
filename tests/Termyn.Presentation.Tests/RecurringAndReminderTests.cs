using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>How typed dates are routed, and what the reminder UI is allowed to offer.</summary>
public class RecurringAndReminderTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- Due text ----------------------------------------------------------------------------------

    [Fact]
    public void A_date_the_local_grammar_knows_is_sent_as_a_date()
    {
        // Resolved here so it still means the right day with no network.
        var presenter = NewPresenter(Store());

        presenter.SetDueFromText("i1", "tomorrow");

        Assert.Equal("2026-08-01", Due(presenter));
    }

    [Fact]
    public void A_recurrence_is_sent_as_the_words_that_were_typed()
    {
        var presenter = NewPresenter(Store());

        presenter.SetDueFromText("i1", "every Monday");

        Assert.Equal("every Monday", Due(presenter));
        
    }

    [Fact]
    public void A_time_inside_a_recurrence_is_not_mistaken_for_a_due_time()
    {
        // A priority ends the recurrence run, so the local grammar reads the "9am" after it as a
        // time of its own and calls it today. That nine o'clock belongs to the schedule, and only
        // the server can say what the schedule means — so the words go over whole.
        var presenter = NewPresenter(Store());

        presenter.SetDueFromText("i1", "every day p1 9am");

        Assert.Equal("every day p1 9am", Due(presenter));
    }

    [Fact]
    public void A_phrasing_the_local_grammar_cannot_read_goes_to_the_server()
    {
        // The local grammar is a deliberate subset; the server understands far more of them.
        var presenter = NewPresenter(Store());

        presenter.SetDueFromText("i1", "in three weeks");

        Assert.Equal("in three weeks", Due(presenter));
    }

    [Fact]
    public void Blank_text_clears_the_due_date()
    {
        var presenter = NewPresenter(Store());

        presenter.SetDueFromText("i1", "   ");

        Assert.Empty(Due(presenter));
    }

    // ---- Rows --------------------------------------------------------------------------------------

    [Fact]
    public void A_row_says_whether_its_task_repeats()
    {
        var presenter = NewPresenter(Store());

        Assert.True(presenter.Rows.Single(r => r.Id == "r1").IsRecurring);
        Assert.False(presenter.Rows.Single(r => r.Id == "i1").IsRecurring);
    }

    [Fact]
    public void A_row_counts_the_reminders_on_its_task()
    {
        var store = Store();
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"relative","minute_offset":30}""");
        store.PutResource("reminders", "m2", """{"id":"m2","item_id":"i1","type":"relative","minute_offset":60}""");
        var presenter = NewPresenter(store);

        Assert.Equal(2, presenter.Rows.Single(r => r.Id == "i1").Reminders);
        Assert.Equal(0, presenter.Rows.Single(r => r.Id == "r1").Reminders);
    }

    // ---- Entitlement -------------------------------------------------------------------------------

    [Fact]
    public void A_free_plan_cannot_add_a_reminder_and_nothing_is_queued()
    {
        // Refused here rather than sent and rejected: §9.4's rule is that the reminder UI never
        // errors on save.
        var presenter = NewPresenter(WithPlan("""{"current":{"plan_name":"free","reminders":false}}"""));

        Assert.False(presenter.RemindersAvailable);
        Assert.False(presenter.AddRelativeReminder("i1", 30));
        Assert.False(presenter.AddAbsoluteReminder("i1", Today, new TimeOnly(9, 0)));
        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.DoesNotContain("pending", presenter.Status);
    }

    [Fact]
    public void A_pro_plan_can_add_a_reminder()
    {
        var presenter = NewPresenter(WithPlan("""{"current":{"plan_name":"pro","reminders":true}}"""));

        Assert.True(presenter.RemindersAvailable);
        Assert.True(presenter.AddRelativeReminder("i1", 30));
        Assert.Single(presenter.RemindersFor("i1"));
    }

    [Fact]
    public void An_account_whose_plan_is_not_known_yet_offers_nothing()
    {
        var presenter = NewPresenter(Store());

        Assert.False(presenter.RemindersAvailable);
        Assert.False(presenter.AddRelativeReminder("i1", 30));
    }

    [Fact]
    public void The_plan_name_is_available_for_saying_what_is_needed()
    {
        var presenter = NewPresenter(WithPlan("""{"current":{"plan_name":"free","reminders":false}}"""));

        Assert.Equal("free", presenter.PlanName);
    }

    [Fact]
    public void Reminders_for_a_task_are_listed_soonest_first()
    {
        var store = WithPlan("""{"current":{"plan_name":"pro","reminders":true}}""");
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"relative","minute_offset":60}""");
        store.PutResource("reminders", "m2", """{"id":"m2","item_id":"i1","type":"relative","minute_offset":10}""");
        store.PutResource("reminders", "m3", """{"id":"m3","item_id":"other","type":"relative","minute_offset":5}""");
        var presenter = NewPresenter(store);

        Assert.Equal([10, 60], presenter.RemindersFor("i1").Select(r => r.MinuteOffset));
    }

    [Fact]
    public void A_reminder_can_be_removed_whatever_the_plan_now_says()
    {
        // A plan that lapsed leaves reminders behind, and being unable to clear them would be a
        // worse answer than letting them go.
        var store = Store();
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"relative","minute_offset":30}""");
        var presenter = NewPresenter(store);

        Assert.False(presenter.RemindersAvailable);
        presenter.DeleteReminder("m1");

        Assert.Empty(presenter.RemindersFor("i1"));
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Ordinary","project_id":"p1","child_order":1,"due":{"date":"2026-07-31"}}""");
        store.PutResource("items", "r1", """{"id":"r1","content":"Water plants","project_id":"p1","child_order":2,"due":{"date":"2026-07-31","string":"every day","is_recurring":true}}""");
        return store;
    }

    private static InMemorySnapshotStore WithPlan(string json)
    {
        var store = Store();
        store.PutResource("user_plan_limits", "user_plan_limits", json);
        return store;
    }

    private static MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }

    /// <summary>
    /// What the row shows for its due date. A recurrence reads back as the words that were typed,
    /// a resolved date as the date — which is exactly the difference being tested.
    /// </summary>
    private static string Due(MainPresenter presenter) => presenter.Rows.Single(r => r.Id == "i1").Due;
}
