using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The moment a reminder is set for, typed into the reminders dialog.
/// </summary>
/// <remarks>
/// Read the way the date boxes read a day, not the way capture reads a task's title: capture keeps
/// what it doesn't read as words, so it never refuses anything, and a reminder has no words to keep
/// them in. Built rather than shown, and pressed through the button's own Click.
/// </remarks>
public class ReminderFormTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [WinFormsFact]
    public void A_day_and_time_typed_sets_a_reminder_then()
    {
        var presenter = Pro();
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        dialog.AddAt("4 aug 2026 at 4pm");

        Assert.Equal("2026-08-04T16:00:00", Single(presenter).DueDate);
        Assert.Equal(string.Empty, dialog.Typed);
    }

    [WinFormsFact]
    public void A_day_with_no_time_on_it_is_at_nine()
    {
        var presenter = Pro();
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        dialog.AddAt("4 aug 2026");

        Assert.Equal("2026-08-04T09:00:00", Single(presenter).DueDate);
    }

    [WinFormsFact]
    public void Words_left_over_mean_the_moment_wasnt_read()
    {
        // Read as a capture, this was tomorrow at nine and "please" was dropped without a word.
        var presenter = Pro();
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        dialog.AddAt("tomorrow please");

        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.Equal(DayBoxText.ForReminder(DayReading.Unread, english: true).Says, dialog.Message);
        Assert.Equal("tomorrow please", dialog.Typed);
    }

    [WinFormsFact]
    public void A_repeat_is_refused_by_name()
    {
        // It used to say it couldn't read the date, which was true and didn't say why.
        var presenter = Pro();
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        dialog.AddAt("every monday 9am");

        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.Equal("A reminder can't repeat", dialog.Message);
    }

    [WinFormsFact]
    public void An_account_that_reads_another_language_is_offered_figures_and_takes_them()
    {
        var presenter = Pro("de");
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        Assert.Equal(DayBoxText.ReminderHint(english: false), dialog.Hint);

        dialog.AddAt("tomorrow 9am");

        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.Equal(DayBoxText.ForReminder(DayReading.Unread, english: false).Says, dialog.Message);

        dialog.AddAt("2026-08-04 16:00");

        Assert.Equal("2026-08-04T16:00:00", Single(presenter).DueDate);
    }

    [WinFormsFact]
    public void Nothing_typed_does_nothing()
    {
        var presenter = Pro(due: Timed);
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        dialog.AddAt("   ");

        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.Equal(string.Empty, dialog.Message);
    }

    [WinFormsFact]
    public void Enter_in_the_box_adds_the_moment_typed_rather_than_closing_over_it()
    {
        // Close is the dialog's accept button, so Enter in the box used to close it and throw the
        // reminder away. Shown, off the screen, since a box nobody has shown can't take the focus.
        var presenter = Pro();
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");
        TestWindow.ShowOffScreen(dialog);

        Assert.True(dialog.EnterAt("4 aug 2026 at 4pm"));

        Assert.Equal("2026-08-04T16:00:00", Single(presenter).DueDate);
        Assert.Equal(DialogResult.None, dialog.DialogResult);
    }

    [WinFormsTheory]
    [InlineData(null)]
    [InlineData(DayOnly)]
    public void Offsets_are_greyed_on_a_task_with_no_due_time_and_it_says_why(string? due)
    {
        // Todoist: "the item should have a due date with time set in order to add a relative reminder".
        using var dialog = ReminderForm.For(Pro(due: due), "i1", "Ship it");

        Assert.False(dialog.OffsetChoiceEnabled);
        Assert.False(dialog.OffsetAddEnabled);
        Assert.Equal(NeedsDueTime, dialog.Message);
        Assert.Equal(NeedsDueTime, dialog.Tip);
    }

    [WinFormsTheory]
    [InlineData(null)]
    [InlineData(DayOnly)]
    public void A_moment_of_its_own_needs_no_due_time(string? due)
    {
        var presenter = Pro(due: due);
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        Assert.True(dialog.MomentBoxEnabled);
        Assert.True(dialog.MomentAddEnabled);

        dialog.AddAt("4 aug 2026 at 4pm");

        Assert.Equal("2026-08-04T16:00:00", Single(presenter).DueDate);
    }

    [WinFormsFact]
    public void Offsets_are_lit_on_a_task_with_a_due_time_and_add_what_was_chosen()
    {
        var presenter = Pro(due: Timed);
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        Assert.True(dialog.OffsetChoiceEnabled);
        Assert.True(dialog.OffsetAddEnabled);
        Assert.True(dialog.MomentBoxEnabled);
        Assert.True(dialog.MomentAddEnabled);
        Assert.Equal(string.Empty, dialog.Message);
        Assert.Equal(string.Empty, dialog.Tip);

        // The third in the list: at the time, then 10 minutes, then 30.
        dialog.AddOffset(2);

        var reminder = Single(presenter);
        Assert.Equal(ReminderKind.Relative, reminder.Kind);
        Assert.Equal(30, reminder.MinuteOffset);
        Assert.Equal(string.Empty, dialog.Message);
    }

    [WinFormsFact]
    public void Why_the_offsets_are_greyed_is_still_said_after_a_moment_is_added()
    {
        // An add clears the line, and the offsets would have been left grey with nothing to say why.
        using var dialog = ReminderForm.For(Pro(), "i1", "Ship it");

        dialog.AddAt("4 aug 2026 at 4pm");

        Assert.False(dialog.OffsetAddEnabled);
        Assert.Equal(NeedsDueTime, dialog.Message);
    }

    [WinFormsFact]
    public void Why_the_offsets_are_greyed_is_said_again_once_a_moment_goes_in_after_one_was_turned_down()
    {
        using var dialog = ReminderForm.For(Pro(), "i1", "Ship it");

        dialog.AddAt("tomorrow please");
        Assert.Equal(DayBoxText.ForReminder(DayReading.Unread, english: true).Says, dialog.Message);

        dialog.AddAt("4 aug 2026 at 4pm");

        Assert.Equal(NeedsDueTime, dialog.Message);
    }

    [WinFormsFact]
    public void Why_the_offsets_are_greyed_is_still_said_after_a_reminder_is_removed()
    {
        var presenter = Pro(reminder: Moment);
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");
        TestWindow.ShowOffScreen(dialog);

        dialog.SelectReminder(0);
        dialog.PressRemove();

        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.False(dialog.OffsetAddEnabled);
        Assert.Equal(NeedsDueTime, dialog.Message);
    }

    [WinFormsFact]
    public void A_due_time_lost_while_the_dialog_was_open_is_found_out_at_the_press()
    {
        // A sync runs behind the dialog, and the button was lit for a due time that's since gone.
        var presenter = Pro(due: Timed);
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");
        presenter.SetDue("i1", new DateOnly(2026, 8, 4));

        dialog.AddOffset(0);

        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.False(dialog.OffsetChoiceEnabled);
        Assert.False(dialog.OffsetAddEnabled);
        Assert.Equal(NeedsDueTime, dialog.Message);
    }

    [WinFormsFact]
    public void A_task_gone_while_the_dialog_was_open_is_said_to_be_gone_rather_than_to_need_a_due_time()
    {
        var presenter = Pro(due: Timed);
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");
        presenter.Delete("i1");

        dialog.AddOffset(0);

        Assert.Equal(Strings.RemindersTaskGone, dialog.Message);
    }

    [WinFormsTheory]
    [InlineData(null)]
    [InlineData(Timed)]
    public void A_plan_without_reminders_greys_everything_that_writes_and_gives_the_plan_as_the_reason(string? due)
    {
        // The plan comes first whatever the due date: adding a due time wouldn't light anything here.
        using var dialog = ReminderForm.For(Holding(Free, due: due), "i1", "Ship it");

        Assert.False(dialog.OffsetChoiceEnabled);
        Assert.False(dialog.OffsetAddEnabled);
        Assert.False(dialog.MomentBoxEnabled);
        Assert.False(dialog.MomentAddEnabled);
        Assert.Equal(Strings.RemindersUpgrade, dialog.Message);
        Assert.Equal(Strings.RemindersUpgrade, dialog.Tip);
    }

    [WinFormsFact]
    public void A_plan_not_heard_of_yet_is_said_to_be_not_synced_rather_than_too_small()
    {
        using var dialog = ReminderForm.For(Holding(plan: null, due: Timed), "i1", "Ship it");

        Assert.False(dialog.OffsetAddEnabled);
        Assert.False(dialog.MomentAddEnabled);
        Assert.Equal(Strings.RemindersPlanNotSynced, dialog.Message);
    }

    [WinFormsFact]
    public void A_plan_that_lapsed_can_still_clear_its_reminders_and_goes_on_saying_why_it_cant_add()
    {
        var presenter = Holding(Free, reminder: Moment);
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");
        TestWindow.ShowOffScreen(dialog);

        dialog.SelectReminder(0);
        Assert.True(dialog.RemoveEnabled);

        dialog.PressRemove();

        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.False(dialog.MomentAddEnabled);
        Assert.Equal(Strings.RemindersUpgrade, dialog.Message);
    }

    private const string NeedsDueTime = "Reminding before it's due needs a due date with a time.";

    /// <summary>A due date with a time, which an offset can count back from.</summary>
    private const string Timed = "2026-08-04T09:00:00";

    /// <summary>A due date with no time, which an offset can't.</summary>
    private const string DayOnly = "2026-08-04";

    /// <summary>A reminder for a moment of its own.</summary>
    private const string Moment = """{"id":"r1","item_id":"i1","type":"absolute","due":{"date":"2026-08-04T16:00:00"}}""";

    private const string ProPlan = """{"current":{"plan_name":"pro","reminders":true}}""";

    private const string Free = """{"current":{"plan_name":"free","reminders":false}}""";

    /// <summary>A presenter on a Pro plan, which may set reminders, holding one task.</summary>
    /// <param name="language">The language the account reads its dates in, or null for no user synced yet</param>
    /// <param name="due">When the task is due, or null for no due date</param>
    /// <param name="reminder">A reminder already on the task, if any</param>
    /// <returns>The presenter</returns>
    private static MainPresenter Pro(string? language = null, string? due = null, string? reminder = null)
        => Holding(ProPlan, language, due, reminder);

    /// <summary>A presenter holding one task.</summary>
    /// <param name="plan">What the account's plan allows, or null for no plan synced yet</param>
    /// <param name="language">The language the account reads its dates in, or null for no user synced yet</param>
    /// <param name="due">When the task is due, or null for no due date</param>
    /// <param name="reminder">A reminder already on the task, if any</param>
    /// <returns>The presenter</returns>
    private static MainPresenter Holding(string? plan, string? language = null, string? due = null, string? reminder = null)
    {
        var dated = due is null ? string.Empty : $$""","due":{"date":"{{due}}"}""";

        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "i1", $$"""{"id":"i1","content":"Ship it","project_id":"p1","child_order":1{{dated}} }""");

        if (plan is not null)
            store.PutResource("user_plan_limits", "user_plan_limits", plan);

        if (reminder is not null)
            store.PutResource("reminders", "r1", reminder);

        if (language is not null)
            store.PutResource("user", "user", $$$"""{"id":"u","lang":"{{{language}}}","tz_info":{"timezone":"UTC"}}""");

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
    }

    /// <summary>The one reminder on the task.</summary>
    private static Reminder Single(MainPresenter presenter) => Assert.Single(presenter.RemindersFor("i1"));
}
