using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// What the history says about things actually done through the presenter.
/// </summary>
/// <remarks>
/// The wording is asserted here rather than in the model's own tests, which know nothing about
/// tasks: this is the half that has to read like something a person would say, and the half that
/// can quietly stop happening when a new action is added and nobody notes it.
/// </remarks>
public class ActionsNotedTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    private static MainPresenter Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("items", "t1", """{"id":"t1","content":"Plan the week","project_id":"p","child_order":1,"due":{"date":"2026-08-01"}}""");
        store.PutResource("items", "t2", """{"id":"t2","content":"Tidy the garage","project_id":"p","child_order":2}""");

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }

    private static string[] Said(MainPresenter presenter)
        => presenter.History.Entries.Select(e => e.Said).ToArray();

    private static string Newest(MainPresenter presenter) => presenter.History.Entries[0].Said;

    // ---- The examples the request gave ---------------------------------------------------------

    [Fact]
    public void Clearing_a_due_date_says_so_and_names_the_task()
    {
        var presenter = Seeded();

        presenter.SetDue("t1", null);

        Assert.Equal("Cleared the due date on “Plan the week”", Newest(presenter));
    }

    [Fact]
    public void Setting_a_due_date_says_the_day_it_was_set_to()
    {
        var presenter = Seeded();

        presenter.SetDue("t1", new DateOnly(2026, 8, 15));

        Assert.Equal("Set “Plan the week” due 15 Aug", Newest(presenter));
    }

    [Fact]
    public void Creating_a_task_says_what_was_typed()
    {
        var presenter = Seeded();

        presenter.CaptureAsync("Buy milk tomorrow").GetAwaiter().GetResult();

        Assert.Equal("Added “Buy milk tomorrow”", Newest(presenter));
    }

    // ---- The rest of it ------------------------------------------------------------------------

    [Fact]
    public void Completing_and_reopening_are_each_said()
    {
        var presenter = Seeded();

        presenter.Complete("t2");
        presenter.Reopen("t2");

        Assert.Equal(["Reopened “Tidy the garage”", "Completed “Tidy the garage”"], Said(presenter));
    }

    [Fact]
    public void A_delete_is_named_before_the_name_goes()
    {
        // Read after the delete there would be nothing left to name it by, and the line would say
        // "a task" about work the user had just been looking at.
        var presenter = Seeded();

        presenter.Delete("t1");

        Assert.Equal("Deleted “Plan the week”", Newest(presenter));
    }

    [Fact]
    public void A_rename_says_what_it_used_to_be_called()
    {
        // The new name is on the row in front of them; the old one is the half they might want back.
        var presenter = Seeded();

        presenter.Rename("t1", "Plan the fortnight");

        Assert.Equal("Renamed “Plan the week” to “Plan the fortnight”", Newest(presenter));
    }

    [Fact]
    public void A_priority_says_which_one_and_clearing_it_says_that_instead()
    {
        var presenter = Seeded();

        presenter.SetPriority("t1", Priority.P1);
        Assert.Equal("Set “Plan the week” to priority 1", Newest(presenter));

        presenter.SetPriority("t1", Priority.P4);
        Assert.Equal("Cleared the priority on “Plan the week”", Newest(presenter));
    }

    [Fact]
    public void A_sub_task_says_what_it_went_under()
    {
        var presenter = Seeded();

        presenter.AddSubtask("t1", "Draft the outline");

        Assert.Equal("Added “Draft the outline” under “Plan the week”", Newest(presenter));
    }

    [Fact]
    public void Structure_changes_are_said_too()
    {
        var presenter = Seeded();

        presenter.AddProject("Home");
        Assert.Equal("Added the project “Home”", Newest(presenter));

        presenter.AddSection("Errands", "p");
        Assert.Equal("Added the section “Errands”", Newest(presenter));

        presenter.AddLabel("urgent");
        Assert.Equal("Added the label “urgent”", Newest(presenter));
    }

    // ---- Not making a fuss, through the presenter ----------------------------------------------

    [Fact]
    public void An_afternoon_on_one_description_leaves_one_line()
    {
        // The thing that was asked for, through the path it actually happens on: the panel writes
        // every time the typing pauses, so this is what forty pauses look like.
        var presenter = Seeded();

        for (var i = 0; i < 40; i++)
            presenter.SetDescription(SubjectKind.Task, "t1", new string('x', i + 1));

        Assert.Equal(["Edited the description of “Plan the week”"], Said(presenter));
    }

    [Fact]
    public void Nudging_a_task_up_the_list_is_one_line_and_not_five()
    {
        var presenter = Seeded();

        for (var i = 0; i < 5; i++)
            presenter.Move("t2", -1);

        Assert.Single(presenter.History.Entries);
    }

    [Fact]
    public void Looking_around_is_not_something_that_was_done()
    {
        // Navigation changes nothing, and a list holding it would bury the things that did.
        var presenter = Seeded();

        presenter.Select(ViewSelection.Of(SmartView.Today));
        presenter.Search("plan");
        presenter.Search(string.Empty);
        presenter.SetCollapsed("t1", true);
        presenter.SortBy(TaskColumn.Content);

        Assert.Empty(presenter.History.Entries);
    }
}
