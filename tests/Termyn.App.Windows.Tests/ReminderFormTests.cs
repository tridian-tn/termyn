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
        var presenter = Pro();
        using var dialog = ReminderForm.For(presenter, "i1", "Ship it");

        dialog.AddAt("   ");

        Assert.Empty(presenter.RemindersFor("i1"));
        Assert.Equal(string.Empty, dialog.Message);
    }

    /// <summary>A presenter on a Pro plan, which may set reminders, holding one task.</summary>
    /// <param name="language">The language the account reads its dates in, or null for no user synced yet</param>
    /// <returns>The presenter</returns>
    private static MainPresenter Pro(string? language = null)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Ship it","project_id":"p1","child_order":1}""");
        store.PutResource("user_plan_limits", "user_plan_limits", """{"current":{"plan_name":"pro","reminders":true}}""");

        if (language is not null)
            store.PutResource("user", "user", $$$"""{"id":"u","lang":"{{{language}}}","tz_info":{"timezone":"UTC"}}""");

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
    }

    /// <summary>The one reminder on the task.</summary>
    private static Reminder Single(MainPresenter presenter) => Assert.Single(presenter.RemindersFor("i1"));
}
