using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// What a task started from a view begins with, which New task puts in the quick-add box.
/// </summary>
/// <remarks>
/// New task and Quick add opened the same empty box, so the two entries did one thing between them.
/// New task now starts from where the user is looking: the project, section or label being viewed,
/// or today's date over Today.
/// </remarks>
public class NewTaskTextTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>Held so where a capture landed can be read off the model.</summary>
    private SyncEngine _engine = null!;

    // ---- Places ----------------------------------------------------------------------------------

    [Fact]
    public void A_project_starts_its_task_in_the_project()
    {
        Assert.Equal("#Work", Seeded().NewTaskText(ViewSelection.OfProject("p")));
    }

    [Fact]
    public void A_name_with_a_space_in_it_is_escaped_the_way_todoist_reads_it()
    {
        // Unescaped, "#Big Shed" is a project called "Big" and a task with "Shed" in its title.
        Assert.Equal(@"#Big\ Shed", Seeded().NewTaskText(ViewSelection.OfProject("q")));
    }

    [Fact]
    public void A_section_names_its_project_as_well()
    {
        // A section's name is only unique inside its project, so on its own it could be anywhere.
        Assert.Equal(@"#Work /Next\ Week", Seeded().NewTaskText(ViewSelection.OfSection("s")));
    }

    [Theory]
    [InlineData("followup", "@followup")]
    [InlineData("on the way", @"@on\ the\ way")]
    public void A_label_starts_its_task_with_the_label(string label, string expected)
    {
        Assert.Equal(expected, Seeded().NewTaskText(ViewSelection.OfLabel(label)));
    }

    [Fact]
    public void The_inbox_is_named_like_any_other_project()
    {
        Assert.Equal("#Inbox", Seeded().NewTaskText(ViewSelection.Of(SmartView.Inbox)));
    }

    [Fact]
    public void The_inbox_starts_with_nothing_before_there_is_one_to_name()
    {
        // Before the first sync. A task naming no project goes to the Inbox anyway, so the entry
        // still works; it just has nothing to write.
        Assert.Equal(string.Empty, NewPresenter(new InMemorySnapshotStore()).NewTaskText(ViewSelection.Of(SmartView.Inbox)));
    }

    [Fact]
    public void A_place_the_account_no_longer_holds_offers_nothing()
    {
        var presenter = Seeded();

        Assert.Null(presenter.NewTaskText(ViewSelection.OfProject("gone")));
        Assert.Null(presenter.NewTaskText(ViewSelection.OfSection("gone")));
    }

    // ---- Today -----------------------------------------------------------------------------------

    [Fact]
    public void Today_starts_its_task_today()
    {
        Assert.Equal("today", Seeded().NewTaskText(ViewSelection.Of(SmartView.Today)));
    }

    [Fact]
    public void Today_is_written_in_figures_for_an_account_reading_another_language()
    {
        // "today" is an English word, and Todoist reads a typed date in the account's own language.
        var presenter = Seeded(language: "de");

        Assert.Equal("2026-07-31", presenter.NewTaskText(ViewSelection.Of(SmartView.Today)));
    }

    [Fact]
    public void Today_offers_nothing_with_smart_date_recognition_off()
    {
        // Todoist would leave the date in the title, so the task would be called "… today" and not
        // be today's at all.
        var presenter = Seeded(language: "en", smartDatesOff: true);

        Assert.Null(presenter.NewTaskText(ViewSelection.Of(SmartView.Today)));
    }

    // ---- Views with nothing to start from ----------------------------------------------------------

    [Theory]
    [InlineData(SmartView.Upcoming)]
    [InlineData(SmartView.All)]
    public void A_view_that_spans_days_or_everything_offers_nothing(SmartView view)
    {
        Assert.Null(Seeded().NewTaskText(ViewSelection.Of(view)));
    }

    [Fact]
    public void A_saved_filter_offers_nothing()
    {
        // A filter is a question about tasks rather than somewhere one can be put.
        Assert.Null(Seeded().NewTaskText(ViewSelection.OfFilter("f1")));
    }

    // ---- Read back -------------------------------------------------------------------------------

    [Fact]
    public async Task A_task_started_in_a_project_lands_in_it_offline()
    {
        // The words have to come back as the place they were written from, and the local grammar is
        // what reads them while the server can't be reached.
        var presenter = Seeded();

        var item = await Captured(presenter, ViewSelection.OfProject("q"));

        Assert.Equal("q", item.ProjectId);
        Assert.Null(item.SectionId);
    }

    [Fact]
    public async Task A_task_started_in_a_section_lands_in_it_offline()
    {
        var presenter = Seeded();

        var item = await Captured(presenter, ViewSelection.OfSection("s"));

        Assert.Equal("p", item.ProjectId);
        Assert.Equal("s", item.SectionId);
    }

    [Fact]
    public async Task A_task_started_on_a_label_carries_it_offline()
    {
        var presenter = Seeded();

        var item = await Captured(presenter, ViewSelection.OfLabel("on the way"));

        Assert.Equal(["on the way"], item.Labels);
    }

    [Fact]
    public async Task A_task_started_on_today_is_due_today_offline()
    {
        var presenter = Seeded();

        var item = await Captured(presenter, ViewSelection.Of(SmartView.Today));

        Assert.Equal("2026-07-31", item.DueDate);
    }

    [Fact]
    public async Task A_task_started_on_today_in_another_language_is_due_today_offline()
    {
        // Online the server reads the figures. Offline a capture for this account reads no day at
        // all, so the box has to say which words it put there for them to be read as one.
        var presenter = Seeded(language: "de");

        var item = await Captured(presenter, ViewSelection.Of(SmartView.Today));

        Assert.Equal("2026-07-31", item.DueDate);
        Assert.Equal("Buy milk", item.Content);
    }

    [Fact]
    public async Task Figures_typed_by_hand_stay_in_the_title_offline_in_another_language()
    {
        // Unchanged: only the box's own start is read. The words around figures the user typed
        // could change what they mean, and the grammar can't read those words.
        var presenter = Seeded(language: "de");

        await presenter.CaptureAsync("Buy milk 2026-07-31", TestContext.Current.CancellationToken);

        var item = _engine.Snapshot().Items.Single();
        Assert.Equal("Buy milk 2026-07-31", item.Content);
        Assert.Null(item.DueDate);
    }

    [Fact]
    public async Task A_day_typed_beside_the_start_is_the_one_kept_offline()
    {
        // The grammar takes the first day it meets and leaves a second in the words. The start
        // mustn't then be read on top of it, giving the task a day other than the one typed.
        var presenter = Seeded();

        await presenter.CaptureAsync("Buy milk tomorrow today", "today", TestContext.Current.CancellationToken);

        Assert.Equal("2026-08-01", _engine.Snapshot().Items.Single().DueDate);
    }

    [Fact]
    public async Task A_start_taken_out_of_the_box_is_not_put_back()
    {
        var presenter = Seeded(language: "de");

        await presenter.CaptureAsync("Buy milk", "2026-07-31", TestContext.Current.CancellationToken);

        var item = _engine.Snapshot().Items.Single();
        Assert.Equal("Buy milk", item.Content);
        Assert.Null(item.DueDate);
    }

    [Fact]
    public void The_preview_finds_the_place_the_start_names()
    {
        // The box shows where the task is going before anything's typed, and an escaped name the
        // preview couldn't find would read as a project that doesn't exist.
        var presenter = Seeded();

        var project = presenter.Preview(" " + presenter.NewTaskText(ViewSelection.OfProject("q")));
        var section = presenter.Preview(" " + presenter.NewTaskText(ViewSelection.OfSection("s")));

        Assert.Equal("Big Shed", project.Parse.ProjectName);
        Assert.True(project.ProjectResolved);
        Assert.Equal("Next Week", section.Parse.SectionName);
        Assert.True(section.ProjectResolved);
        Assert.True(section.SectionResolved);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds "Buy milk" from a view, the way the box sends it: what's typed, then what it started with.
    /// </summary>
    /// <param name="presenter">The presenter to add through, which can't reach the server</param>
    /// <param name="view">The view the task is started from</param>
    /// <returns>The task as the model holds it</returns>
    private async Task<TaskItem> Captured(MainPresenter presenter, ViewSelection view)
    {
        var start = presenter.NewTaskText(view);
        Assert.NotNull(start);

        await presenter.CaptureAsync("Buy milk " + start, start, TestContext.Current.CancellationToken);

        return _engine.Snapshot().Items.Single(i => i.Content == "Buy milk");
    }

    /// <summary>
    /// Two projects, one with a space in its name, a section with one, two labels, a filter and the
    /// Inbox — and an account reading its dates however the test says.
    /// </summary>
    /// <param name="language">The account's language, or null for no user synced yet</param>
    /// <param name="smartDatesOff">Whether the account has turned smart date recognition off</param>
    /// <returns>The presenter, showing everything</returns>
    private MainPresenter Seeded(string? language = null, bool smartDatesOff = false)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "in", """{"id":"in","name":"Inbox","inbox_project":true,"child_order":0}""");
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("projects", "q", """{"id":"q","name":"Big Shed","child_order":2}""");
        store.PutResource("sections", "s", """{"id":"s","name":"Next Week","project_id":"p"}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup"}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"on the way"}""");
        store.PutResource("filters", "f1", """{"id":"f1","name":"Urgent","query":"p1"}""");

        if (language is not null)
            store.PutResource("user", "user", $$$"""{"id":"u","lang":"{{{language}}}","features":{"dateist_inline_disabled":{{{(smartDatesOff ? "true" : "false")}}}},"tz_info":{"timezone":"UTC"}}""");

        return NewPresenter(store);
    }

    private MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        _engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        _engine.Load();
        var presenter = new MainPresenter(_engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
