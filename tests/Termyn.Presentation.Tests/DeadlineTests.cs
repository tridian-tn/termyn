using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The date a task has to be finished by, as the outline shows it and orders by it.
/// </summary>
/// <remarks>
/// Read-only throughout: Todoist sets a deadline, Termyn shows it. Nothing here writes one, which
/// is the whole of what v1 owes.
/// </remarks>
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

    private static MainPresenter Presenter(params (string Id, string Json)[] tasks)
    {
        var store = new InMemorySnapshotStore();
        foreach (var task in tasks)
            store.PutResource("items", task.Id, task.Json);

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
