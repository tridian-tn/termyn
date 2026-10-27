using System.Globalization;
using System.Text.Json.Nodes;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The Schedule menu: which day each entry lands on, how the entries read, and what moving a task
/// keeps.
/// </summary>
public class ScheduleTests
{
    /// <summary>A Friday.</summary>
    private static readonly DateOnly Today = new(2026, 10, 30);

    // ---- Which day ---------------------------------------------------------------------------------

    [Theory]

    // Friday: the weekend is tomorrow, and next week is Monday.
    [InlineData(2026, 10, 30, "2026-10-31", "2026-11-02")]

    // On Saturday itself the weekend is the next one, since putting something off until the
    // weekend has to move it.
    [InlineData(2026, 10, 31, "2026-11-07", "2026-11-02")]

    // Sunday: next week is tomorrow.
    [InlineData(2026, 11, 1, "2026-11-07", "2026-11-02")]

    // Monday: next week is the Monday after, never today.
    [InlineData(2026, 11, 2, "2026-11-07", "2026-11-09")]
    public void The_weekend_and_next_week_are_always_ahead_of_today(int year, int month, int day, string weekend, string nextWeek)
    {
        var days = ScheduleDays.From(new DateOnly(year, month, day), DayOfWeek.Saturday, DayOfWeek.Monday);

        Assert.Equal(DateOnly.Parse(weekend, CultureInfo.InvariantCulture), days.For(AppCommand.DueWeekend));
        Assert.Equal(DateOnly.Parse(nextWeek, CultureInfo.InvariantCulture), days.For(AppCommand.DueNextWeek));
    }

    [Fact]
    public void The_days_are_the_accounts_own_rather_than_saturday_and_monday()
    {
        // An account can put its weekend on Friday and its next week on Sunday, and Todoist's own
        // menu goes by that.
        var days = ScheduleDays.From(Today, DayOfWeek.Friday, DayOfWeek.Sunday);

        Assert.Equal(new DateOnly(2026, 11, 6), days.For(AppCommand.DueWeekend));
        Assert.Equal(new DateOnly(2026, 11, 1), days.For(AppCommand.DueNextWeek));
    }

    [Fact]
    public void Today_and_tomorrow_need_nothing_from_the_account()
    {
        var days = ScheduleDays.From(Today, null, null);

        Assert.Equal(Today, days.For(AppCommand.DueToday));
        Assert.Equal(Today.AddDays(1), days.For(AppCommand.DueTomorrow));
        Assert.Null(days.For(AppCommand.DueWeekend));
        Assert.Null(days.For(AppCommand.DueNextWeek));
    }

    // ---- How the entries read ------------------------------------------------------------------------

    private static TaskRow Row(string due = "", DateOnly? dueOn = null)
        => new("i1", "Write it up", Priority.P4, "Work", due, [], DueOn: dueOn);

    private static CommandState State(AppCommand command, TaskRow? row, ScheduleDays? days)
        => Commands.StateOf(command, new CommandContext(row, Schedule: days));

    [Fact]
    public void Each_entry_says_which_day_it_lands_on()
    {
        using var _ = new CultureScope("en-GB");
        var days = ScheduleDays.From(new DateOnly(2026, 10, 27), DayOfWeek.Saturday, DayOfWeek.Monday);

        // The weekday alone for the next two days, and the date with it further out, punctuated the
        // way the region's long date is.
        Assert.Equal("Tue", State(AppCommand.DueToday, Row(), days).Hint);
        Assert.Equal("Wed", State(AppCommand.DueTomorrow, Row(), days).Hint);
        Assert.Equal("Sat, 31 Oct", State(AppCommand.DueWeekend, Row(), days).Hint);
        Assert.Equal("Mon, 2 Nov", State(AppCommand.DueNextWeek, Row(), days).Hint);
        Assert.Null(State(AppCommand.DueNone, Row(), days).Hint);
    }

    [Fact]
    public void The_day_the_task_is_already_due_is_ticked()
    {
        var days = ScheduleDays.From(Today, DayOfWeek.Saturday, DayOfWeek.Monday);
        var row = Row("2 Nov", new DateOnly(2026, 11, 2));

        Assert.True(State(AppCommand.DueNextWeek, row, days).Checked);
        Assert.False(State(AppCommand.DueToday, row, days).Checked);
        Assert.False(State(AppCommand.DueTomorrow, row, days).Checked);
    }

    [Fact]
    public void An_entry_the_account_has_not_given_a_day_for_is_greyed()
    {
        var days = ScheduleDays.From(Today, null, null);

        Assert.True(State(AppCommand.DueToday, Row(), days).Enabled);
        Assert.False(State(AppCommand.DueWeekend, Row(), days).Enabled);
        Assert.False(State(AppCommand.DueNextWeek, Row(), days).Enabled);
    }

    [Fact]
    public void Nothing_is_offered_without_a_task()
    {
        var days = ScheduleDays.From(Today, DayOfWeek.Saturday, DayOfWeek.Monday);

        Assert.All(
            new[] { AppCommand.DueToday, AppCommand.DueTomorrow, AppCommand.DueWeekend, AppCommand.DueNextWeek, AppCommand.DueNone },
            c => Assert.False(State(c, null, days).Enabled));
    }

    [Fact]
    public void No_date_is_greyed_on_a_task_with_no_due_date_to_take_away()
    {
        Assert.False(State(AppCommand.DueNone, Row(), null).Enabled);
        Assert.True(State(AppCommand.DueNone, Row("every monday"), null).Enabled);
    }

    // ---- What moving a task keeps --------------------------------------------------------------------

    [Fact]
    public void The_days_come_from_the_accounts_settings()
    {
        var presenter = NewPresenter(Store()).Presenter;

        Assert.Equal(new DateOnly(2026, 10, 31), presenter.ScheduleDays.For(AppCommand.DueWeekend));
        Assert.Equal(new DateOnly(2026, 11, 2), presenter.ScheduleDays.For(AppCommand.DueNextWeek));
    }

    [Fact]
    public void Moving_a_task_keeps_the_time_it_is_due_at()
    {
        var (engine, presenter) = NewPresenter(Store());

        Assert.True(presenter.Reschedule("timed", new DateOnly(2026, 11, 2)));

        var args = JsonNode.Parse(Assert.Single(engine.Outbox).ArgsJson)!.AsObject();
        Assert.Equal("2026-11-02T16:30:00", args["due"]!["date"]!.ToString());
        Assert.Equal(new DateOnly(2026, 11, 2), presenter.Rows.Single(r => r.Id == "timed").DueOn);
    }

    [Fact]
    public void Moving_a_repeat_keeps_it_repeating()
    {
        var (engine, presenter) = NewPresenter(Store());

        presenter.Reschedule("repeat", new DateOnly(2026, 11, 2));

        var args = JsonNode.Parse(Assert.Single(engine.Outbox).ArgsJson)!.AsObject();
        Assert.Equal("every day", args["due"]!["string"]!.ToString());

        var row = presenter.Rows.Single(r => r.Id == "repeat");
        Assert.True(row.IsRecurring);
        Assert.Equal(new DateOnly(2026, 11, 2), row.DueOn);
    }

    [Fact]
    public void Moving_a_task_to_the_day_it_is_already_due_writes_nothing()
    {
        // Nothing to tell Todoist, and nothing to note in what's been done.
        var (engine, presenter) = NewPresenter(Store());

        Assert.False(presenter.Reschedule("timed", new DateOnly(2026, 10, 30)));
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public void Moving_a_task_is_noted_in_what_has_been_done()
    {
        var presenter = NewPresenter(Store()).Presenter;

        presenter.Reschedule("plain", new DateOnly(2026, 11, 2));

        Assert.Contains("2 Nov", Assert.Single(presenter.History.Recent()).Said);
    }

    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("user", "user", """{"id":"u1","weekend_start_day":6,"next_week":1,"tz_info":{"timezone":"UTC"}}""");
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("items", "plain", """{"id":"plain","content":"Plain","project_id":"p1","child_order":1}""");
        store.PutResource("items", "timed", """{"id":"timed","content":"Timed","project_id":"p1","child_order":2,"due":{"date":"2026-10-30T16:30:00","string":"today 16:30","is_recurring":false}}""");
        store.PutResource("items", "repeat", """{"id":"repeat","content":"Water plants","project_id":"p1","child_order":3,"due":{"date":"2026-10-30","string":"every day","lang":"en","is_recurring":true}}""");
        return store;
    }

    private static (SyncEngine Engine, MainPresenter Presenter) NewPresenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return (engine, presenter);
    }
}
