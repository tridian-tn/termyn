using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The date a task has to be finished by: setting it, clearing it, and how the outline shows it
/// and orders by it.
/// </summary>
public class DeadlineTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- What the column says ------------------------------------------------------------------

    [Fact]
    public void A_deadline_is_written_the_way_the_due_date_is()
    {
        var row = Single(Task("i1", "Ship it", deadline: "2026-08-04"));

        Assert.Equal("4 Aug", row.Deadline);
        Assert.Equal(new DateOnly(2026, 8, 4), row.DeadlineOn);
    }

    [Fact]
    public void A_deadline_in_another_year_says_which()
    {
        // The year is only worth the width when it isn't this one — the same rule the due column
        // follows, so the two read alike down the row.
        Assert.Equal("4 Aug 2027", Single(Task("i1", "Ship it", deadline: "2027-08-04")).Deadline);
    }

    [Fact]
    public void A_task_with_no_deadline_leaves_the_column_empty()
    {
        var row = Single(Task("i1", "Ship it"));

        Assert.Equal(string.Empty, row.Deadline);
        Assert.Null(row.DeadlineOn);
    }

    [Fact]
    public void A_deadline_is_its_own_date_rather_than_the_due_one()
    {
        // The two answer different questions — when to pick the work up, and when it stops being
        // any use — and a task can carry either without the other.
        var row = Single(Task("i1", "Ship it", due: "2026-08-01", deadline: "2026-08-04"));

        Assert.Equal("1 Aug", row.Due);
        Assert.Equal("4 Aug", row.Deadline);
    }

    // ---- Ordering by it ------------------------------------------------------------------------

    [Fact]
    public void Sorting_by_deadline_leads_with_the_nearest()
    {
        var presenter = Presenter(
            Task("a", "Latest", deadline: "2026-09-01"),
            Task("b", "Soonest", deadline: "2026-08-02"),
            Task("c", "Middling", deadline: "2026-08-20"));

        presenter.SortBy(TaskColumn.Deadline);

        Assert.Equal(["Soonest", "Middling", "Latest"], Contents(presenter));
    }

    [Fact]
    public void The_order_follows_the_deadline_and_not_the_due_date()
    {
        var presenter = Presenter(
            Task("a", "Due first", due: "2026-08-01", deadline: "2026-09-01"),
            Task("b", "Due second", due: "2026-08-02", deadline: "2026-08-05"));

        presenter.SortBy(TaskColumn.Deadline);

        Assert.Equal(["Due second", "Due first"], Contents(presenter));
    }

    [Fact]
    public void A_task_with_no_deadline_sits_at_the_bottom_whichever_way_the_column_points()
    {
        var presenter = Presenter(
            Task("a", "None"),
            Task("b", "Earlier", deadline: "2026-08-02"),
            Task("c", "Later", deadline: "2026-09-01"));

        presenter.SortBy(TaskColumn.Deadline);
        Assert.Equal(["Earlier", "Later", "None"], Contents(presenter));

        // No deadline isn't the most distant one — the task hasn't got one, so turning the column
        // round must not lift it to the top.
        presenter.SortBy(TaskColumn.Deadline);
        Assert.Equal(["Later", "Earlier", "None"], Contents(presenter));
    }

    // ---- Setting one ---------------------------------------------------------------------------

    [Fact]
    public void A_day_picked_is_set_as_the_deadline()
    {
        var presenter = Presenter(Task("i1", "Ship it"));

        presenter.SetDeadline("i1", new DateOnly(2026, 8, 4));

        Assert.Equal("4 Aug", presenter.Rows.Single().Deadline);
    }

    [Fact]
    public void What_reaches_the_account_is_the_day_alone_on_the_one_field()
    {
        // The row moving only proves the local copy changed. This is the half that goes to Todoist,
        // and a command carrying more than the one field would take a stale copy of the rest with it.
        var (engine, presenter) = Engine(Task("i1", "Ship it"));

        presenter.SetDeadline("i1", new DateOnly(2026, 8, 4));

        var queued = Assert.Single(engine.Outbox);
        Assert.Equal("item_update", queued.Type);

        var args = JsonNode.Parse(queued.ArgsJson)!.AsObject();
        Assert.Equal(["deadline", "id"], args.Select(a => a.Key).Order().ToArray());
        Assert.Equal("2026-08-04", args["deadline"]!["date"]!.ToString());
    }

    [Fact]
    public void Clearing_it_empties_the_column_and_says_so_to_the_account()
    {
        var (engine, presenter) = Engine(Task("i1", "Ship it", deadline: "2026-08-04"));

        presenter.SetDeadline("i1", null);

        Assert.Equal(string.Empty, presenter.Rows.Single().Deadline);

        // A null rather than an absent field: leaving it out is the one thing that means "don't
        // touch it", which would leave the deadline exactly where it was.
        var args = JsonNode.Parse(Assert.Single(engine.Outbox).ArgsJson)!.AsObject();
        Assert.True(args.ContainsKey("deadline"));
        Assert.Null(args["deadline"]);
    }

    [Fact]
    public void Setting_and_clearing_are_written_down_in_what_youve_done()
    {
        var presenter = Presenter(Task("i1", "Ship it"), Task("i2", "Something else"));

        presenter.SetDeadline("i1", new DateOnly(2026, 8, 4));
        presenter.SetDeadline("i2", null);

        Assert.Equal(
            ["Cleared the deadline on “Something else”", "Set “Ship it” to finish by 4 Aug"],
            presenter.History.Recent().Select(e => e.Said).ToArray());
    }

    [Fact]
    public void Changing_your_mind_about_one_deadline_is_a_single_line()
    {
        // The same work on the same task, the way a run of description edits is: the list keeps
        // the wording it ended up with rather than every step on the way there.
        var presenter = Presenter(Task("i1", "Ship it"));

        presenter.SetDeadline("i1", new DateOnly(2026, 8, 4));
        presenter.SetDeadline("i1", new DateOnly(2026, 8, 11));

        Assert.Equal(["Set “Ship it” to finish by 11 Aug"], presenter.History.Recent().Select(e => e.Said).ToArray());
    }

    [Fact]
    public void Setting_a_deadline_leaves_the_due_date_alone()
    {
        var presenter = Presenter(Task("i1", "Ship it", due: "2026-08-01"));

        presenter.SetDeadline("i1", new DateOnly(2026, 8, 4));

        var row = presenter.Rows.Single();
        Assert.Equal("1 Aug", row.Due);
        Assert.Equal("4 Aug", row.Deadline);
    }

    [Fact]
    public void A_deadline_is_set_on_the_task_it_was_asked_for_and_no_other()
    {
        var presenter = Presenter(Task("i1", "Ship it"), Task("i2", "Something else"));

        presenter.SetDeadline("i2", new DateOnly(2026, 8, 4));

        Assert.Equal([string.Empty, "4 Aug"], presenter.Rows.Select(r => r.Deadline).ToArray());
    }

    [Fact]
    public void The_day_offered_is_the_accounts_today_rather_than_the_machines()
    {
        // What the picker opens on for a task with no deadline. Read from the account's own clock
        // so it agrees with the rest of the app about which day it is.
        Assert.Equal(Today, Presenter(Task("i1", "Ship it")).Today);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static string[] Contents(MainPresenter presenter)
        => presenter.Rows.Select(r => r.Content).ToArray();

    /// <summary>A task as the server stores one, with the dates it was given.</summary>
    /// <param name="id">The task's id, which also orders it against the others written here</param>
    /// <param name="content">What the task says</param>
    /// <param name="due">The day it's due, or null for a task with no due date</param>
    /// <param name="deadline">The day it has to be finished by, or null for a task with none</param>
    /// <returns>The item's raw JSON, for the snapshot store</returns>
    private static (string Id, string Json) Task(string id, string content, string? due = null, string? deadline = null)
    {
        var fields = new List<string> { $"\"id\":\"{id}\"", $"\"content\":\"{content}\"" };

        if (due is not null) fields.Add($"\"due\":{{\"date\":\"{due}\"}}");

        // As Todoist sends it: an object of its own, with a plain calendar date and the language it
        // was written in.
        if (deadline is not null) fields.Add($"\"deadline\":{{\"date\":\"{deadline}\",\"lang\":\"en\"}}");

        return (id, "{" + string.Join(",", fields) + "}");
    }

    private static TaskRow Single((string Id, string Json) task) => Presenter(task).Rows.Single();

    private static MainPresenter Presenter(params (string Id, string Json)[] tasks) => Engine(tasks).Presenter;

    /// <summary>The same presenter, with the engine behind it, for the tests that read the outbox.</summary>
    private static (SyncEngine Engine, MainPresenter Presenter) Engine(params (string Id, string Json)[] tasks)
    {
        var store = new InMemorySnapshotStore();
        foreach (var task in tasks)
            store.PutResource("items", task.Id, task.Json);

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return (engine, presenter);
    }
}
