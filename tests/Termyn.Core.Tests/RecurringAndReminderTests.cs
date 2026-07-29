using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>Recurring tasks, and reminders gated on what the account's plan allows.</summary>
public class RecurringAndReminderTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- Recurring ---------------------------------------------------------------------------------

    [Fact]
    public void Closing_a_recurring_task_leaves_it_open()
    {
        // The server moves it to its next occurrence; it isn't finished. Ticking it off here would
        // take the row out of the list until a sync put it back.
        var engine = Seeded();

        engine.CompleteItem("r1");

        Assert.False(engine.Snapshot().Items.Single(i => i.Id == "r1").Completed);
        Assert.Equal("item_close", engine.Outbox.Single().Type);
    }

    [Fact]
    public void Closing_a_recurring_task_does_not_guess_its_next_date()
    {
        // Working out where "every day" lands next is the server's job, and the whole reason a
        // recurrence is written as words rather than picked.
        var engine = Seeded();

        engine.CompleteItem("r1");

        var task = engine.Snapshot().Items.Single(i => i.Id == "r1");
        Assert.Equal("2026-07-31", task.DueDate);
        Assert.Equal("every day", task.DueText);
    }

    [Fact]
    public void Closing_an_ordinary_task_still_ticks_it_off()
    {
        var engine = Seeded();

        engine.CompleteItem("i1");

        Assert.True(engine.Snapshot().Items.Single(i => i.Id == "i1").Completed);
    }

    [Fact]
    public void Closing_a_recurring_task_can_still_be_undone()
    {
        var engine = Seeded();
        engine.CompleteItem("r1");

        Assert.True(engine.Undo());
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_recurring_task_is_recognised_from_its_due_object()
    {
        var engine = Seeded();

        Assert.True(engine.Snapshot().Items.Single(i => i.Id == "r1").IsRecurring);
        Assert.False(engine.Snapshot().Items.Single(i => i.Id == "i1").IsRecurring);
    }

    [Fact]
    public void A_due_string_is_sent_as_words_for_the_server_to_read()
    {
        var engine = Seeded();

        engine.UpdateItem("i1", new JsonObject { ["due"] = Termyn.Core.Capture.ItemFields.DueString("every Monday") });

        var due = Args(engine.Outbox.Single())["due"]!.AsObject();
        Assert.Equal("every Monday", due["string"]!.ToString());
        Assert.Null(due["date"]); // no date invented alongside it
    }

    // ---- Plan entitlement --------------------------------------------------------------------------

    [Fact]
    public void Reminders_are_unavailable_until_the_plan_says_otherwise()
    {
        // A fresh install knows nothing about the account yet, and offering a feature the server
        // then refuses is the one thing the reminder UI must not do.
        var engine = NewEngine(new InMemorySnapshotStore());

        Assert.Null(engine.Snapshot().PlanLimits);
        Assert.False(engine.Snapshot().RemindersAvailable);
    }

    [Fact]
    public void A_free_plan_reports_reminders_as_unavailable()
    {
        var engine = WithPlan("""{"current":{"plan_name":"free","reminders":false,"reminders_at_due":true,"max_reminders_time":0}}""");

        Assert.False(engine.Snapshot().RemindersAvailable);
        Assert.Equal("free", engine.Snapshot().PlanLimits!.PlanName);
    }

    [Fact]
    public void A_pro_plan_reports_reminders_as_available()
    {
        var engine = WithPlan("""{"current":{"plan_name":"pro","reminders":true,"max_reminders_time":700}}""");

        Assert.True(engine.Snapshot().RemindersAvailable);
        Assert.Equal(700, engine.Snapshot().PlanLimits!.MaxTimeReminders);
    }

    [Fact]
    public void The_plan_that_counts_is_the_current_one_not_the_upgrade()
    {
        // The resource carries the plan you could move to alongside the one you have.
        var engine = WithPlan("""{"current":{"plan_name":"free","reminders":false},"next":{"plan_name":"pro","reminders":true}}""");

        Assert.False(engine.Snapshot().RemindersAvailable);
    }

    // ---- Reminders ---------------------------------------------------------------------------------

    [Fact]
    public void Adding_a_relative_reminder_shows_it_and_queues_reminder_add()
    {
        var engine = Seeded();

        engine.AddRelativeReminder("i1", 30);

        var reminder = engine.Snapshot().Reminders.Single();
        Assert.Equal("i1", reminder.ItemId);
        Assert.Equal(ReminderKind.Relative, reminder.Kind);
        Assert.Equal(30, reminder.MinuteOffset);

        var cmd = engine.Outbox.Single();
        Assert.Equal("reminder_add", cmd.Type);
        Assert.Equal("relative", Args(cmd)["type"]!.ToString());
    }

    [Fact]
    public void Adding_an_absolute_reminder_sends_a_moment_of_its_own()
    {
        var engine = Seeded();

        engine.AddAbsoluteReminder("i1", new DateOnly(2026, 8, 3), new TimeOnly(9, 0));

        Assert.Equal(ReminderKind.Absolute, engine.Snapshot().Reminders.Single().Kind);
        Assert.Equal("2026-08-03T09:00:00", Args(engine.Outbox.Single())["due"]!["date"]!.ToString());
    }

    [Fact]
    public void Deleting_a_reminder_takes_it_off_and_queues_reminder_delete()
    {
        var store = Store();
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"relative","minute_offset":30}""");
        var engine = NewEngine(store);

        engine.DeleteReminder("m1");

        Assert.Empty(engine.Snapshot().Reminders);
        Assert.Equal("reminder_delete", engine.Outbox.Single().Type);
    }

    [Fact]
    public void A_queued_reminder_delete_can_be_reverted()
    {
        var store = Store();
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"relative","minute_offset":30}""");
        var engine = NewEngine(store);
        engine.DeleteReminder("m1");

        engine.Revert(engine.Outbox.Single().Uuid);

        Assert.Single(engine.Snapshot().Reminders);
    }

    [Fact]
    public void Deleting_a_reminder_that_is_not_there_does_nothing()
    {
        var engine = Seeded();

        engine.DeleteReminder("ghost");

        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void A_location_reminder_is_read_but_not_something_Termyn_authors()
    {
        var store = Store();
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"location","name":"Office","loc_lat":"51.5","loc_long":"-0.1"}""");
        var engine = NewEngine(store);

        var reminder = engine.Snapshot().Reminders.Single();
        Assert.Equal(ReminderKind.Location, reminder.Kind);
        Assert.Equal("Office", reminder.LocationName);
    }

    [Fact]
    public async Task A_reminder_created_offline_keeps_its_task_after_the_reconnect()
    {
        var store = Store();
        var api = new FakeApi();
        var engine = NewEngine(store, api);

        var temp = engine.AddRelativeReminder("i1", 30);

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            TempIdMapping = new Dictionary<string, string> { [temp] = "M9" },
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };
        await engine.SyncAsync();

        var reminder = engine.Snapshot().Reminders.Single();
        Assert.Equal("M9", reminder.Id);
        Assert.Equal("i1", reminder.ItemId);
        Assert.Equal(0, engine.PendingCount);
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Ordinary","project_id":"p","due":{"date":"2026-07-31"}}""");
        store.PutResource("items", "r1", """{"id":"r1","content":"Water plants","project_id":"p","due":{"date":"2026-07-31","string":"every day","is_recurring":true}}""");
        return store;
    }

    private static SyncEngine Seeded() => NewEngine(Store());

    private static SyncEngine WithPlan(string json)
    {
        var store = Store();
        store.PutResource("user_plan_limits", "user_plan_limits", json);
        return NewEngine(store);
    }

    private static SyncEngine NewEngine(InMemorySnapshotStore store, FakeApi? api = null)
    {
        var engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return engine;
    }

    private static JsonObject Args(OutboxCommand cmd) => JsonNode.Parse(cmd.ArgsJson)!.AsObject();
}
