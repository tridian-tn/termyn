using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
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
            TempIdMapping = new Dictionary<string, string> { [temp!] = "M9" },
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };
        await engine.SyncAsync();

        var reminder = engine.Snapshot().Reminders.Single();
        Assert.Equal("M9", reminder.Id);
        Assert.Equal("i1", reminder.ItemId);
        Assert.Equal(0, engine.PendingCount);
    }

    // ---- Regressions -------------------------------------------------------------------------------

    [Fact]
    public void Rewriting_a_schedule_keeps_the_date_and_the_repeat()
    {
        // due is one object, so sending only the string used to replace the lot — which dropped
        // is_recurring and left the very next close free to tick the task off.
        var engine = Seeded();

        engine.SetItemDueString("r1", "every Monday", recurring: true);

        var task = engine.Snapshot().Items.Single(i => i.Id == "r1");
        Assert.Equal("every Monday", task.DueText);
        Assert.Equal("2026-07-31", task.DueDate); // until the server resolves the new schedule
        Assert.True(task.IsRecurring);

        engine.CompleteItem("r1");
        Assert.False(engine.Snapshot().Items.Single(i => i.Id == "r1").Completed);
    }

    [Fact]
    public void Rewriting_a_schedule_sends_only_the_words()
    {
        var engine = Seeded();

        engine.SetItemDueString("i1", "every Monday", recurring: true);

        var due = Args(engine.Outbox.Single())["due"]!.AsObject();
        Assert.Equal("every Monday", due["string"]!.ToString());
        Assert.Null(due["date"]); // the server resolves it; inventing one here would be a guess
    }

    [Fact]
    public void A_second_close_is_not_queued_while_the_first_is_waiting()
    {
        // A recurring row doesn't change when it is closed, so pressing again is the natural
        // response — and each close the server takes skips another occurrence.
        var engine = Seeded();

        engine.CompleteItem("r1");
        engine.CompleteItem("r1");

        Assert.Single(engine.Outbox);
    }

    [Fact]
    public async Task The_advanced_occurrence_lands_even_when_the_close_is_not_acked()
    {
        // The close changes nothing locally, so it owns nothing — and must not hold off the
        // server's version of a task it never touched. The token moves on either way.
        var store = Store();
        var api = new FakeApi();
        var engine = NewEngine(store, api);

        engine.CompleteItem("r1");

        api.Response = new SyncResponse
        {
            SyncToken = "s2",
            Changes = [new ResourceChange("items", "r1", false, Json.Object("""{"id":"r1","content":"Water plants","due":{"date":"2026-08-01","string":"every day","is_recurring":true}}"""))],
        };
        await engine.SyncAsync();

        Assert.Equal("2026-08-01", engine.Snapshot().Items.Single(i => i.Id == "r1").DueDate);
    }

    [Fact]
    public async Task Undo_does_not_claim_to_reverse_a_close_the_server_has_taken()
    {
        // The occurrence is gone and item_uncomplete would reopen a task that was never closed.
        // Reporting success while changing nothing is the worst of the options.
        var store = Store();
        var api = new FakeApi();
        var engine = NewEngine(store, api);

        engine.CompleteItem("r1");
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };
        await engine.SyncAsync();

        Assert.False(engine.Undo());
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public void A_queued_recurring_close_is_still_undoable_after_a_restart()
    {
        var store = Store();
        NewEngine(store).CompleteItem("r1");

        var restarted = NewEngine(store);

        Assert.True(restarted.CanUndo);
        Assert.True(restarted.Undo());
        Assert.Equal(0, restarted.PendingCount);
    }

    [Fact]
    public void Undo_does_not_reach_past_a_reminder_delete()
    {
        var store = Store();
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"relative","minute_offset":30}""");
        var engine = NewEngine(store);

        engine.CompleteItem("i1");
        engine.DeleteReminder("m1");

        // The delete is the last thing done, so it is the first thing undone — not the completion
        // underneath it.
        Assert.True(engine.Undo());
        Assert.Single(engine.Snapshot().Reminders);
        Assert.True(engine.Snapshot().Items.Single(i => i.Id == "i1").Completed);
    }

    [Fact]
    public void A_reminder_is_not_queued_for_a_task_that_is_not_held()
    {
        var engine = Seeded();

        Assert.Null(engine.AddRelativeReminder("ghost", 30));
        Assert.Equal(0, engine.PendingCount);
        Assert.Empty(engine.Snapshot().Reminders);
    }

    [Fact]
    public async Task A_reminder_follows_its_task_when_the_task_gets_its_real_id()
    {
        // item_id is a reference like any other, and a reminder created alongside an offline task
        // is left pointing at a temp id nothing holds if nothing rewrites it.
        var store = new InMemorySnapshotStore();
        var api = new FakeApi();
        var engine = NewEngine(store, api);

        var taskId = engine.AddItem(new JsonObject { ["content"] = "Written offline" });
        engine.AddRelativeReminder(taskId, 30);

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            TempIdMapping = new Dictionary<string, string> { [taskId] = "I9" },
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };
        await engine.SyncAsync();

        Assert.Equal("I9", engine.Snapshot().Reminders.Single().ItemId);
    }

    [Fact]
    public async Task A_task_the_server_refuses_takes_its_reminder_with_it()
    {
        var store = new InMemorySnapshotStore();
        var api = new FakeApi();
        var engine = NewEngine(store, api);

        var taskId = engine.AddItem(new JsonObject { ["content"] = "Written offline" });
        engine.AddRelativeReminder(taskId, 30);

        // The task is rejected; the reminder hangs off it and can only fail on its own.
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands
                .Where(c => c.Type == "item_add")
                .ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected")),
        };
        await engine.SyncAsync();

        Assert.Empty(engine.Snapshot().Reminders);
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public void An_upgrade_that_asks_for_more_resources_resyncs_from_scratch()
    {
        // A resource type added in a later version never arrives on an incremental sync, because
        // nothing about it changed. Without this the feature built on it is dead for everyone who
        // already had the app.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Task"}""");
        store.SaveSync([], [], "s-from-an-older-version");

        Assert.Equal("*", NewEngine(store).SyncToken);
    }

    [Fact]
    public void A_cache_holding_every_resource_keeps_its_place()
    {
        var store = Store();
        store.PutResource("user", "user", """{"id":"u","tz_info":{"timezone":"Europe/London"}}""");
        store.PutResource("user_plan_limits", "user_plan_limits", """{"current":{"plan_name":"pro","reminders":true}}""");
        store.SaveSync([], [], "s-current");

        Assert.Equal("s-current", NewEngine(store).SyncToken);
    }

    [Fact]
    public void A_reminder_kind_Termyn_does_not_know_is_kept_as_unknown()
    {
        // Guessing "relative" would describe it wrongly and, worse, offer to delete something
        // Termyn could never put back.
        var store = Store();
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"something_new"}""");
        var engine = NewEngine(store);

        Assert.Equal(ReminderKind.Unknown, engine.Snapshot().Reminders.Single().Kind);
    }

    [Fact]
    public void An_offline_capture_of_a_recurrence_does_not_invent_a_one_off_date()
    {
        // A priority ends the recurrence run, so the "9am" after it reads as a bare time and a
        // bare time means today — filing a repeating task as a one-off due this morning.
        var parse = new QuickAddParser(new FixedClock(Today)).Parse("Water plants every day p1 9am");

        Assert.True(parse.IsRecurrence);
        Assert.NotNull(parse.DueDate); // the parser still finds one, which is exactly the trap
        Assert.Null(ItemFields.ForAdd(parse)["due"]);
    }

    // ---- Second round ------------------------------------------------------------------------------

    [Fact]
    public async Task The_resync_after_an_upgrade_leaves_nothing_stale_behind()
    {
        // Forcing a full sync over a cache that already has content was a new situation: a full
        // sync is the live set with no tombstones, so without pruning, anything deleted while the
        // old version was installed survived for ever and the token moved past its tombstone.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Still there"}""");
        store.PutResource("items", "gone", """{"id":"gone","content":"Deleted on the web"}""");
        store.SaveSync([], [], "s-from-an-older-version");

        var api = new FakeApi();
        var engine = NewEngine(store, api);

        api.Response = new SyncResponse
        {
            SyncToken = "s2",
            FullSync = true,
            Changes = [new ResourceChange("items", "i1", false, Json.Object("""{"id":"i1","content":"Still there"}"""))],
        };
        await engine.SyncAsync();

        Assert.Equal(["i1"], engine.Snapshot().Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_full_sync_keeps_what_a_queued_write_owns()
    {
        // A task created offline isn't in the server's live set for a reason of our own making.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Known"}""");
        store.SaveSync([], [], "s-old");

        var api = new FakeApi();
        var engine = NewEngine(store, api);
        engine.AddItem(new JsonObject { ["content"] = "Written offline" });

        api.Response = new SyncResponse { SyncToken = "s2", FullSync = true, Changes = [] };
        await engine.SyncAsync();

        Assert.Contains(engine.Snapshot().Items, i => i.Content == "Written offline");
    }

    [Fact]
    public void A_queued_reminder_delete_is_still_a_barrier_after_a_restart()
    {
        // The barrier was recorded when the delete was made but not when the outbox was reloaded,
        // so it lasted only as long as the session did.
        var store = Store();
        store.PutResource("reminders", "m1", """{"id":"m1","item_id":"i1","type":"relative","minute_offset":30}""");

        var engine = NewEngine(store);
        engine.CompleteItem("i1");
        engine.DeleteReminder("m1");

        var restarted = NewEngine(store);

        Assert.True(restarted.Undo());
        Assert.Single(restarted.Snapshot().Reminders);
        Assert.True(restarted.Snapshot().Items.Single(i => i.Id == "i1").Completed);
    }

    [Fact]
    public void Making_a_task_repeat_makes_the_next_close_advance_it()
    {
        // The merge kept an is_recurring it was given but never set one, so a task the user had
        // just made repeating still looked ordinary — and the next close ticked it off.
        var engine = Seeded();

        engine.SetItemDueString("i1", "every Monday", recurring: true);
        Assert.True(engine.Snapshot().Items.Single(i => i.Id == "i1").IsRecurring);

        engine.CompleteItem("i1");
        Assert.False(engine.Snapshot().Items.Single(i => i.Id == "i1").Completed);
    }

    [Fact]
    public void Taking_a_task_off_a_repeat_makes_the_next_close_finish_it()
    {
        // The other direction. Preserving the old flag whenever the new words weren't a repeat left
        // a task the user had just given a plain schedule still looking like it recurred, so the
        // close after it advanced a task the server was about to finish.
        var engine = Seeded();

        engine.SetItemDueString("r1", "in three weeks", recurring: false);
        Assert.False(engine.Snapshot().Items.Single(i => i.Id == "r1").IsRecurring);

        engine.CompleteItem("r1");
        Assert.True(engine.Snapshot().Items.Single(i => i.Id == "r1").Completed);
    }

    [Fact]
    public void A_schedule_shows_the_same_words_that_were_sent()
    {
        var engine = Seeded();

        engine.SetItemDueString("i1", "  every Monday  ", recurring: true);

        Assert.Equal("every Monday", engine.Snapshot().Items.Single(i => i.Id == "i1").DueText);
        Assert.Equal("every Monday", Args(engine.Outbox.Single())["due"]!["string"]!.ToString());
    }

    [Fact]
    public void A_schedule_can_be_set_on_a_task_that_had_no_date()
    {
        var store = Store();
        store.PutResource("items", "n1", """{"id":"n1","content":"No date"}""");
        var engine = NewEngine(store);

        engine.SetItemDueString("n1", "every Monday", recurring: true);

        var task = engine.Snapshot().Items.Single(i => i.Id == "n1");
        Assert.Equal("every Monday", task.DueText);
        Assert.Null(task.DueDate);
        Assert.True(task.IsRecurring);
    }

    [Fact]
    public async Task A_close_can_be_repeated_while_an_earlier_one_is_on_the_wire()
    {
        // Undo can't withdraw a command already being sent, so it reopens the task instead — which
        // leaves the close pending while the task is visibly open. Guarding every close on that
        // would swallow the next press, and the user would be left looking at the row they had
        // just ticked off. Only a recurring close needs the guard: an ordinary row leaves the list.
        var store = Store();
        var api = new FakeApi();
        var engine = NewEngine(store, api);

        engine.CompleteItem("i1");

        api.Next = _ =>
        {
            // Both of these land while the first close is in flight.
            engine.Undo();
            engine.CompleteItem("i1");
            return new SyncResponse { SyncToken = "s2" };
        };
        await engine.SyncAsync();

        Assert.True(engine.Snapshot().Items.Single(i => i.Id == "i1").Completed);
    }

    [Fact]
    public void Only_the_singleton_the_cache_lacks_needs_to_be_missing()
    {
        // Either one absent means the cache predates the set the client now asks for.
        var withoutPlan = Store();
        withoutPlan.PutResource("user", "user", """{"id":"u"}""");
        withoutPlan.SaveSync([], [], "s-old");
        Assert.Equal("*", NewEngine(withoutPlan).SyncToken);

        var withoutUser = Store();
        withoutUser.PutResource("user_plan_limits", "user_plan_limits", """{"current":{"plan_name":"pro","reminders":true}}""");
        withoutUser.SaveSync([], [], "s-old");
        Assert.Equal("*", NewEngine(withoutUser).SyncToken);
    }

    [Fact]
    public void The_resync_check_only_looks_for_resources_the_client_asks_for()
    {
        // If it watched for something never requested, the server would never send it and every
        // start would full-sync for ever.
        Assert.Contains(ResourceType.User, ResourceType.All);
        Assert.Contains(ResourceType.UserPlanLimits, ResourceType.All);
    }

    [Fact]
    public async Task A_reminder_the_server_sends_arrives_and_its_tombstone_takes_it_away()
    {
        var store = Store();
        var api = new FakeApi();
        var engine = NewEngine(store, api);

        api.Response = new SyncResponse
        {
            SyncToken = "s2",
            Changes = [new ResourceChange("reminders", "M1", false, Json.Object("""{"id":"M1","item_id":"i1","type":"relative","minute_offset":45}"""))],
        };
        await engine.SyncAsync();
        Assert.Equal(45, engine.Snapshot().Reminders.Single().MinuteOffset);

        api.Response = new SyncResponse
        {
            SyncToken = "s3",
            Changes = [new ResourceChange("reminders", "M1", true, Json.Object("""{"id":"M1"}"""))],
        };
        await engine.SyncAsync();
        Assert.Empty(engine.Snapshot().Reminders);
    }

    [Fact]
    public async Task The_plan_the_server_sends_reaches_the_snapshot()
    {
        // The singleton is keyed by its own type name, which is the one thing joining what the API
        // client writes to what the model reads.
        var store = Store();
        var api = new FakeApi();
        var engine = NewEngine(store, api);

        api.Response = new SyncResponse
        {
            SyncToken = "s2",
            Changes = [new ResourceChange("user_plan_limits", "user_plan_limits", false, Json.Object("""{"current":{"plan_name":"pro","reminders":true,"max_reminders_time":700}}"""))],
        };
        await engine.SyncAsync();

        Assert.True(engine.Snapshot().RemindersAvailable);
        Assert.Equal(700, engine.Snapshot().PlanLimits!.MaxTimeReminders);
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
