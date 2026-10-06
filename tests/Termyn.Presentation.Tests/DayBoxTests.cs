using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The presenter's half of asking for a day: reading what's typed by the account's own settings,
/// opening the due-date box on the date a task has, and what each box says about what it read.
/// </summary>
public class DayBoxTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- The account's settings reach the grammar --------------------------------------------------

    [Theory]
    [InlineData(0, 2027, 6, 5)]
    [InlineData(1, 2027, 5, 6)]
    public void A_due_date_in_figures_is_read_in_the_accounts_order(int dateFormat, int year, int month, int day)
    {
        var presenter = NewPresenter(WithUser($$$"""{"id":"u","date_format":{{{dateFormat}}},"tz_info":{"timezone":"UTC"}}"""));

        presenter.SetDueFromText("i1", "5/6");

        Assert.Equal(new DateOnly(year, month, day), Row(presenter).DueOn);
    }

    [Fact]
    public void A_due_date_in_figures_goes_to_Todoist_as_words_when_the_order_isnt_known()
    {
        // It can't be told here whether "5/6" is June or May, and the server can.
        var presenter = NewPresenter(Store());

        presenter.SetDueFromText("i1", "5/6");

        Assert.Equal("5/6", Row(presenter).Due);
    }

    [Fact]
    public void Next_friday_is_read_by_where_the_accounts_week_starts()
    {
        var presenter = NewPresenter(WithUser("""{"id":"u","start_day":1,"tz_info":{"timezone":"UTC"}}"""));

        presenter.SetDueFromText("i1", "next friday");

        Assert.Equal(new DateOnly(2026, 8, 7), Row(presenter).DueOn);
    }

    [Fact]
    public async Task An_offline_capture_reads_its_day_by_the_accounts_settings_too()
    {
        // The same grammar as the due-date box, with the same settings: one reading of the words,
        // wherever they were typed.
        var presenter = NewPresenter(WithUser("""{"id":"u","date_format":1,"tz_info":{"timezone":"UTC"}}"""));

        await presenter.CaptureAsync("Renew passport 5/6");

        var row = presenter.Rows.Single(r => r.Content == "Renew passport");
        Assert.Equal(new DateOnly(2027, 5, 6), row.DueOn);
    }

    [Fact]
    public void Reading_a_box_uses_the_accounts_settings()
    {
        var presenter = NewPresenter(WithUser("""{"id":"u","start_day":1,"tz_info":{"timezone":"UTC"}}"""));

        Assert.Equal(DayReading.On(new DateOnly(2026, 8, 7)), presenter.ReadDay("next fri"));
    }

    [Fact]
    public void A_day_typed_with_a_priority_goes_to_Todoist_whole_rather_than_losing_the_priority()
    {
        // It used to set tomorrow and drop the p1 without a word: everything left over is the tell
        // that the words weren't understood here.
        var presenter = NewPresenter(Store());

        presenter.SetDueFromText("i1", "tomorrow p1");

        Assert.Equal("tomorrow p1", Row(presenter).Due);
    }

    // ---- What the due-date box opens on ------------------------------------------------------------

    [Fact]
    public void A_day_opens_as_words_the_box_reads_back()
    {
        var presenter = NewPresenter(Store());

        Assert.Equal("31 Jul 2026", presenter.DueWritten("i1"));
    }

    [Fact]
    public void A_time_on_the_day_opens_with_it()
    {
        var store = Store();
        store.PutResource("items", "i2", """{"id":"i2","content":"Call","project_id":"p1","child_order":3,"due":{"date":"2026-08-04T16:30:00","string":"4 Aug 16:30"}}""");
        var presenter = NewPresenter(store);

        Assert.Equal("4 Aug 2026 16:30", presenter.DueWritten("i2"));
    }

    [Fact]
    public void A_fixed_time_opens_in_the_accounts_timezone()
    {
        // Stored as the instant in UTC; 15:30 in London's summer is 16:30 there.
        var store = WithUser("""{"id":"u","tz_info":{"timezone":"Europe/London"}}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Call","project_id":"p1","child_order":3,"due":{"date":"2026-08-04T15:30:00Z","timezone":"Europe/London"}}""");
        var presenter = NewPresenter(store);

        Assert.Equal("4 Aug 2026 16:30", presenter.DueWritten("i2"));
    }

    [Fact]
    public void A_repeat_opens_on_its_own_words()
    {
        var presenter = NewPresenter(Store());

        Assert.Equal("every day", presenter.DueWritten("r1"));
    }

    [Fact]
    public void Words_still_waiting_for_the_server_open_as_themselves()
    {
        // Set offline, "in three weeks" has no day until the server reads it. Opened empty, the box
        // hid it, and with nothing to clear it couldn't be taken back before it synced.
        var store = Store();
        store.PutResource("items", "i2", """{"id":"i2","content":"Someday","project_id":"p1","child_order":3}""");
        var presenter = NewPresenter(store);

        presenter.SetDueFromText("i2", "in three weeks");

        Assert.Equal("in three weeks", presenter.DueWritten("i2"));
    }

    [Fact]
    public void A_task_with_no_due_date_opens_on_an_empty_box()
    {
        var store = Store();
        store.PutResource("items", "i2", """{"id":"i2","content":"Someday","project_id":"p1","child_order":3}""");
        var presenter = NewPresenter(store);

        Assert.Equal(string.Empty, presenter.DueWritten("i2"));
        Assert.Equal(string.Empty, presenter.DueWritten("missing"));
    }

    // ---- What each box says ------------------------------------------------------------------------

    [Fact]
    public void A_due_date_takes_anything()
    {
        // What isn't read here goes to Todoist as the words, which is how a repeat is set at all.
        Assert.All(Readings(), reading => Assert.True(DayBoxText.ForDue(reading).Accepted));
    }

    [Fact]
    public void A_deadline_takes_a_whole_day_or_nothing()
    {
        Assert.True(DayBoxText.ForDeadline(DayReading.Blank).Accepted);
        Assert.True(DayBoxText.ForDeadline(DayReading.On(Today)).Accepted);

        Assert.False(DayBoxText.ForDeadline(DayReading.On(Today, new TimeOnly(9, 0))).Accepted);
        Assert.False(DayBoxText.ForDeadline(DayReading.Repeat).Accepted);
        Assert.False(DayBoxText.ForDeadline(DayReading.Unread).Accepted);
    }

    [Fact]
    public void Every_reading_is_described_by_both_boxes()
    {
        // A blank line under the box would leave whoever typed something wondering what it meant.
        Assert.All(Readings(), reading =>
        {
            Assert.NotEmpty(DayBoxText.ForDue(reading).Says);
            Assert.NotEmpty(DayBoxText.ForDeadline(reading).Says);
        });
    }

    [Fact]
    public void A_day_is_described_in_full_with_its_time()
    {
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-GB");

            Assert.Equal("Tuesday, 4 August 2026 at 16:30", DayBoxText.ForDue(DayReading.On(new DateOnly(2026, 8, 4), new TimeOnly(16, 30))).Says);
            Assert.Equal("Tuesday, 4 August 2026", DayBoxText.ForDeadline(DayReading.On(new DateOnly(2026, 8, 4))).Says);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void A_reminder_takes_a_day_and_nothing_else()
    {
        // A reminder is a moment, set once: a repeat is refused by name, and words left over mean
        // the moment wasn't read.
        Assert.True(DayBoxText.ForReminder(DayReading.On(Today)).Accepted);
        Assert.True(DayBoxText.ForReminder(DayReading.On(Today, new TimeOnly(16, 0))).Accepted);

        Assert.False(DayBoxText.ForReminder(DayReading.Repeat).Accepted);
        Assert.False(DayBoxText.ForReminder(DayReading.Unread).Accepted);
        Assert.False(DayBoxText.ForReminder(DayReading.Blank).Accepted);

        Assert.Equal("A reminder can't repeat", DayBoxText.ForReminder(DayReading.Repeat).Says);
        Assert.NotEmpty(DayBoxText.ForReminder(DayReading.Unread).Says);
    }

    [Fact]
    public void A_reminder_on_a_day_with_no_time_is_described_at_nine()
    {
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-GB");

            Assert.Equal("Tuesday, 4 August 2026 at 09:00", DayBoxText.ForReminder(DayReading.On(new DateOnly(2026, 8, 4))).Says);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void Every_example_the_hint_gives_is_one_the_box_reads()
    {
        // The hint offered "fri", which isn't read on its own — so the deadline box refused its own
        // example, and a due date typed from it went without a date until the next sync.
        var presenter = NewPresenter(Store());

        foreach (var example in DayBoxText.Hint.Split(", "))
            Assert.Equal(DayReadingKind.Day, presenter.ReadDay(example).Kind);
    }

    [Fact]
    public void A_day_the_machines_calendar_cant_hold_is_still_described()
    {
        // Saudi Arabia's Umm al-Qura calendar runs from 1900 to 2077. Formatting outside it threw, and
        // the box describes its day as it opens, so a deadline from an old import couldn't be opened.
        var was = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");

            Assert.NotEmpty(DayBoxText.ForDeadline(DayReading.On(new DateOnly(1600, 5, 1))).Says);
            Assert.NotEmpty(DayBoxText.ForDue(DayReading.On(new DateOnly(2100, 1, 1), new TimeOnly(9, 0))).Says);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = was;
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static DayReading[] Readings()
        => [DayReading.Blank, DayReading.On(Today), DayReading.On(Today, new TimeOnly(9, 0)), DayReading.Repeat, DayReading.Unread];

    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Ordinary","project_id":"p1","child_order":1,"due":{"date":"2026-07-31"}}""");
        store.PutResource("items", "r1", """{"id":"r1","content":"Water plants","project_id":"p1","child_order":2,"due":{"date":"2026-07-31","string":"every day","is_recurring":true}}""");
        return store;
    }

    private static InMemorySnapshotStore WithUser(string json)
    {
        var store = Store();
        store.PutResource("user", "user", json);
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

    private static TaskRow Row(MainPresenter presenter) => presenter.Rows.Single(r => r.Id == "i1");
}
