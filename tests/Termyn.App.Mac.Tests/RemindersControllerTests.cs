using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// The reminders popover, with its view loaded but not shown: what it lists, how the two ways of
/// adding go, and what a plan without reminders leaves on offer.
/// </summary>
public class RemindersControllerTests
{
    private static readonly (string, bool) Pro = ("pro", true);
    private static readonly (string, bool) Free = ("free", false);

    private readonly List<string> _closes = [];

    private RemindersController Open(MainPresenter presenter, string id = "a")
        => new(presenter, FakeAccount.Commands(presenter), id, () => _closes.Add("closed"));

    private static void Holding(InMemorySnapshotStore store, params string[] reminders)
    {
        foreach (var reminder in reminders)
            store.PutResource("reminders", Id(reminder), reminder);
    }

    /// <summary>A task with a due time, which an offset can count back from.</summary>
    private static void Timed(InMemorySnapshotStore store)
        => store.PutResource("items", "t", """{"id":"t","content":"Timed","project_id":"p1","child_order":3,"due":{"date":"2026-08-04T09:00:00"}}""");

    private static string Id(string reminder)
        => System.Text.Json.JsonDocument.Parse(reminder).RootElement.GetProperty("id").GetString()!;

    [AppKitFact]
    public void With_none_it_says_so_and_offers_nothing_to_remove()
    {
        using var controller = Open(FakeAccount.Presenter(plan: Pro));

        var row = Assert.Single(Rows(controller));
        Assert.Equal("No reminders on this task.", Words(row).StringValue);
        Assert.True(Remove(row).Hidden);
    }

    [AppKitFact]
    public void It_lists_the_tasks_reminders_in_words()
    {
        var presenter = FakeAccount.Presenter(plan: Pro, prepare: store => Holding(
            store,
            """{"id":"r1","item_id":"a","type":"relative","minute_offset":30}""",
            """{"id":"r2","item_id":"b","type":"relative","minute_offset":10}"""));
        using var controller = Open(presenter);

        var row = Assert.Single(Rows(controller));
        Assert.Equal("30 minutes before it's due", Words(row).StringValue);
        Assert.False(Remove(row).Hidden);
    }

    [AppKitFact]
    public void A_kind_it_couldnt_put_back_isnt_offered_for_removal()
    {
        var presenter = FakeAccount.Presenter(plan: Pro, prepare: store => Holding(
            store,
            """{"id":"r1","item_id":"a","type":"location","name":"Home"}"""));
        using var controller = Open(presenter);

        Assert.True(Remove(Assert.Single(Rows(controller))).Hidden);
    }

    [AppKitFact]
    public void Add_beside_the_offsets_adds_the_one_chosen()
    {
        var presenter = FakeAccount.Presenter(plan: Pro, prepare: Timed);
        using var controller = Open(presenter, "t");
        _ = controller.View;

        Offsets(controller).SelectItem(3);
        Click(AddButtons(controller)[0]);

        var added = Assert.Single(presenter.RemindersFor("t"));
        Assert.Equal(ReminderKind.Relative, added.Kind);
        Assert.Equal(60, added.MinuteOffset);
        Assert.Equal("1 hour before it's due", Words(Assert.Single(Rows(controller))).StringValue);
    }

    [AppKitFact]
    public void Return_adds_the_moment_typed_and_empties_the_field()
    {
        var presenter = FakeAccount.Presenter(plan: Pro);
        using var controller = Open(presenter);

        Return(controller, "2026-08-04 16:00");

        Assert.Equal("2026-08-04T16:00:00", Assert.Single(presenter.RemindersFor("a")).DueDate);
        Assert.Equal(string.Empty, Field(controller).StringValue);
        Assert.Empty(_closes);
    }

    [AppKitFact]
    public void A_day_with_no_time_on_it_is_at_nine()
    {
        var presenter = FakeAccount.Presenter(plan: Pro);
        using var controller = Open(presenter);

        Return(controller, "2026-08-04");

        Assert.Equal("2026-08-04T09:00:00", Assert.Single(presenter.RemindersFor("a")).DueDate);
    }

    [AppKitFact]
    public void Add_beside_the_field_is_lit_only_with_something_typed()
    {
        using var controller = Open(FakeAccount.Presenter(plan: Pro));
        _ = controller.View;
        Assert.False(AddButtons(controller)[1].Enabled);

        Type(controller, "2026-08-04");

        Assert.True(AddButtons(controller)[1].Enabled);
        Click(AddButtons(controller)[1]);
        Assert.Single(Rows(controller));
        Assert.False(AddButtons(controller)[1].Enabled);
    }

    [AppKitFact]
    public void What_isnt_a_moment_is_refused_and_left_to_be_put_right()
    {
        var presenter = FakeAccount.Presenter(plan: Pro);
        using var controller = Open(presenter);

        foreach (var typed in new[] { "every monday 9am", "whenever suits" })
        {
            Return(controller, typed);

            Assert.Empty(presenter.RemindersFor("a"));
            Assert.Equal(typed, Field(controller).StringValue);
            Assert.Equal(DayBoxText.ForReminder(presenter.ReadDay(typed), presenter.DatesInEnglish).Says, Message(controller).StringValue);
            Assert.Equal(NSColor.SystemRed, Message(controller).TextColor);
            Assert.Empty(_closes);
        }
    }

    [AppKitFact]
    public void Return_with_nothing_typed_closes_it()
    {
        using var controller = Open(FakeAccount.Presenter(plan: Pro));

        Return(controller, "   ");

        Assert.Single(_closes);
    }

    [AppKitFact]
    public void Esc_closes_it()
    {
        using var controller = Open(FakeAccount.Presenter(plan: Pro));
        _ = controller.View;

        Press(controller, "cancelOperation:");

        Assert.Single(_closes);
    }

    [AppKitFact]
    public void The_button_on_a_row_removes_that_reminder()
    {
        var presenter = FakeAccount.Presenter(plan: Pro, prepare: store => Holding(
            store,
            """{"id":"r1","item_id":"a","type":"relative","minute_offset":30}""",
            """{"id":"r2","item_id":"a","type":"relative","minute_offset":10}"""));
        using var controller = Open(presenter);

        Click(Remove(Rows(controller)[0]));

        Assert.Equal("r2", Assert.Single(presenter.RemindersFor("a")).Id);
        Assert.Equal("10 minutes before it's due", Words(Assert.Single(Rows(controller))).StringValue);
    }

    [AppKitFact]
    public async Task It_keeps_up_with_a_task_the_server_has_just_renamed()
    {
        // A sub-task added a moment ago goes by a name of ours until a sync hears the server's, and
        // a list asked by the old name would say the task has no reminders.
        var api = new FakeApi();
        var presenter = FakeAccount.Presenter(api, plan: Pro);
        var temporary = presenter.AddSubtask("a", "New")!;
        using var controller = Open(presenter, temporary);
        Return(controller, "2026-08-04 16:00");

        api.Next = FakeApi.Naming(new Dictionary<string, string> { ["item_add"] = "real1", ["reminder_add"] = "rem1" });
        await presenter.SyncAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(temporary, presenter.CurrentIdOf(temporary));

        Return(controller, "2026-08-05 16:00");

        Assert.Equal(2, presenter.RemindersFor("real1").Count);
        Assert.Equal(2, Rows(controller).Count);
    }

    [AppKitFact]
    public void Removing_one_takes_back_a_refusal_for_being_at_the_limit()
    {
        var presenter = FakeAccount.Presenter(prepare: store =>
        {
            store.PutResource("user_plan_limits", "user_plan_limits", """{"current":{"plan_name":"pro","reminders":true,"max_reminders_time":1}}""");
            Holding(store, """{"id":"r1","item_id":"a","type":"relative","minute_offset":30}""");
        });
        using var controller = Open(presenter);

        Return(controller, "2026-08-04 16:00");
        Assert.Equal("This plan is at its limit for reminders.", Message(controller).StringValue);

        Click(Remove(Assert.Single(Rows(controller))));

        Assert.Equal(DayBoxText.ForReminder(presenter.ReadDay(Field(controller).StringValue), presenter.DatesInEnglish).Says, Message(controller).StringValue);
    }

    [AppKitFact]
    public async Task A_sync_while_it_is_open_brings_the_plan_and_the_reminders_up_to_date()
    {
        var api = new FakeApi();
        var presenter = FakeAccount.Presenter(api, plan: Free, prepare: store =>
        {
            Timed(store);
            Holding(store, """{"id":"r1","item_id":"t","type":"relative","minute_offset":30}""");
        });
        using var controller = Open(presenter, "t");
        _ = controller.View;
        controller.ViewWillAppear();
        Assert.False(Field(controller).Enabled);

        api.Response = new SyncResponse
        {
            SyncToken = "s1",
            Changes =
            [
                new ResourceChange("user_plan_limits", "user_plan_limits", false, new JsonObject { ["current"] = new JsonObject { ["plan_name"] = "pro", ["reminders"] = true } }),
                new ResourceChange("reminders", "r1", true, new JsonObject { ["id"] = "r1" }),
            ],
        };
        await presenter.SyncAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.True(Field(controller).Enabled);
        Assert.True(Offsets(controller).Enabled);
        Assert.Equal(string.Empty, Message(controller).StringValue);
        Assert.Equal("No reminders on this task.", Words(Assert.Single(Rows(controller))).StringValue);

        controller.ViewDidDisappear();
    }

    [AppKitFact]
    public async Task It_stops_following_once_it_has_gone()
    {
        var api = new FakeApi();
        var presenter = FakeAccount.Presenter(api, plan: Free);
        using var controller = Open(presenter);
        _ = controller.View;
        controller.ViewWillAppear();
        controller.ViewDidDisappear();

        api.Response = new SyncResponse
        {
            SyncToken = "s1",
            Changes = [new ResourceChange("user_plan_limits", "user_plan_limits", false, new JsonObject { ["current"] = new JsonObject { ["plan_name"] = "pro", ["reminders"] = true } })],
        };
        await presenter.SyncAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(Field(controller).Enabled);
    }

    [AppKitFact]
    public void Offsets_are_greyed_on_a_task_with_no_due_time_and_it_says_why()
    {
        // Todoist: "the item should have a due date with time set in order to add a relative reminder".
        var presenter = FakeAccount.Presenter(plan: Pro);

        foreach (var id in new[] { "a", "b" })
        {
            using var controller = Open(presenter, id);
            _ = controller.View;

            Assert.False(Offsets(controller).Enabled);
            Assert.False(AddButtons(controller)[0].Enabled);
            Assert.True(Field(controller).Enabled);
            Assert.Equal("Reminding before it's due needs a due date with a time.", Message(controller).StringValue);
        }
    }

    [AppKitFact]
    public void Offsets_are_lit_on_a_task_with_a_due_time()
    {
        using var controller = Open(FakeAccount.Presenter(plan: Pro, prepare: Timed), "t");
        _ = controller.View;

        Assert.True(Offsets(controller).Enabled);
        Assert.True(AddButtons(controller)[0].Enabled);
        Assert.Equal(string.Empty, Message(controller).StringValue);
    }

    [AppKitFact]
    public void The_arrows_and_backspace_remove_a_reminder_from_the_field()
    {
        var presenter = FakeAccount.Presenter(plan: Pro, prepare: store => Holding(
            store,
            """{"id":"r1","item_id":"a","type":"relative","minute_offset":30}""",
            """{"id":"r2","item_id":"a","type":"relative","minute_offset":10}"""));
        using var controller = Open(presenter);
        _ = controller.View;

        Press(controller, "moveDown:");
        Press(controller, "moveDown:");
        Assert.Equal(1, Table(controller).SelectedRow);

        Press(controller, "deleteBackward:");

        Assert.Equal("r1", Assert.Single(presenter.RemindersFor("a")).Id);
        Assert.Equal(0, Table(controller).SelectedRow);

        Press(controller, "deleteBackward:");

        Assert.Empty(presenter.RemindersFor("a"));
        Assert.Equal(-1, Table(controller).SelectedRow);
    }

    [AppKitFact]
    public void Backspace_edits_what_is_typed_rather_than_removing_a_reminder()
    {
        var presenter = FakeAccount.Presenter(plan: Pro, prepare: store => Holding(
            store,
            """{"id":"r1","item_id":"a","type":"relative","minute_offset":30}"""));
        using var controller = Open(presenter);
        _ = controller.View;

        Press(controller, "moveDown:");
        Type(controller, "2026");
        Press(controller, "deleteBackward:");

        Assert.Single(presenter.RemindersFor("a"));
    }

    [AppKitFact]
    public void On_a_plan_without_them_adding_is_greyed_and_says_why()
    {
        using var controller = Open(FakeAccount.Presenter(plan: Free));
        _ = controller.View;

        Assert.False(Offsets(controller).Enabled);
        Assert.False(Field(controller).Enabled);
        Assert.All(AddButtons(controller), add => Assert.False(add.Enabled));
        Assert.Equal("Reminders need Todoist Pro.", Message(controller).StringValue);
    }

    [AppKitFact]
    public void A_plan_not_heard_of_yet_isnt_told_to_upgrade()
    {
        using var controller = Open(FakeAccount.Presenter());
        _ = controller.View;

        Assert.False(Field(controller).Enabled);
        Assert.Equal("Your plan hasn't synced yet.", Message(controller).StringValue);
    }

    [AppKitFact]
    public void Removal_still_works_on_a_plan_that_lapsed()
    {
        var presenter = FakeAccount.Presenter(plan: Free, prepare: store => Holding(
            store,
            """{"id":"r1","item_id":"a","type":"relative","minute_offset":30}"""));
        using var controller = Open(presenter);

        Click(Remove(Assert.Single(Rows(controller))));

        Assert.Empty(presenter.RemindersFor("a"));
    }

    private static NSTableView Table(NSViewController controller)
        => Assert.IsType<NSTableView>(controller.View.Subviews.OfType<NSScrollView>().Single().DocumentView, exactMatch: false);

    /// <summary>Each row's cell, made if the list hasn't needed it yet.</summary>
    private static List<NSView> Rows(NSViewController controller)
    {
        var table = Table(controller);
        return Enumerable.Range(0, (int)table.RowCount).Select(row => table.GetView(0, row, makeIfNecessary: true)!).ToList();
    }

    private static NSTextField Words(NSView row) => row.Subviews.OfType<NSTextField>().Single();

    private static NSButton Remove(NSView row) => row.Subviews.OfType<NSButton>().Single();

    private static NSGridView Form(NSViewController controller) => controller.View.Subviews.OfType<NSGridView>().Single();

    private static NSPopUpButton Offsets(NSViewController controller) => Form(controller).Subviews.OfType<NSPopUpButton>().Single();

    private static NSTextField Field(NSViewController controller) => Form(controller).Subviews.OfType<NSTextField>().Single(f => f.Editable);

    /// <summary>The Add buttons: the offsets', then the field's.</summary>
    private static List<NSButton> AddButtons(NSViewController controller)
        => Form(controller).Subviews.OfType<NSButton>().Where(b => b is not NSPopUpButton).ToList();

    /// <summary>The line under the form, the last thing in the popover.</summary>
    private static NSTextField Message(NSViewController controller) => (NSTextField)controller.View.Subviews[^1];

    private static void Click(NSButton button) => button.PerformClick(button);

    private static void Type(NSViewController controller, string typed) => PopoverKeys.Type(Field(controller), typed);

    private static void Return(NSViewController controller, string typed) => PopoverKeys.Return(Field(controller), typed);

    private static void Press(NSViewController controller, string selector) => PopoverKeys.Press(Field(controller), selector);
}
