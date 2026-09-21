using System.Globalization;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Upcoming, under a heading for each day.
/// </summary>
/// <remarks>
/// The outline is a virtualised list, which can't use the control's own grouping — so a heading is
/// a row like any other, and what these cover is that it only ever appears where it should and
/// never stands in for a task.
/// </remarks>
public class UpcomingByDayTests
{
    /// <summary>A Friday, so the days after it are named rather than numbered alike.</summary>
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public void Each_day_is_headed_before_the_tasks_due_on_it()
    {
        var presenter = Upcoming(
            Task("a", "Second day", "2026-08-02"),
            Task("b", "First day", "2026-08-01"),
            Task("c", "Also second", "2026-08-02"));

        Assert.Equal(
            ["1 Aug · Tomorrow", "First day", "2 Aug · Sunday", "Second day", "Also second"],
            Contents(presenter));
    }

    [Fact]
    public void The_day_after_today_is_named_rather_than_numbered()
    {
        // How anybody would say it. The rest are weekdays, which is what you'd say for a day this
        // side of the week after.
        var presenter = Upcoming(Task("a", "Soon", "2026-08-01"));

        Assert.Equal("1 Aug · Tomorrow", Contents(presenter)[0]);
    }

    [Fact]
    public void A_heading_is_not_a_task()
    {
        var presenter = Upcoming(Task("a", "Soon", "2026-08-01"));

        var heading = presenter.Rows[0];

        Assert.True(heading.IsHeading);
        Assert.Empty(heading.Project);
        Assert.Empty(heading.Due);
        Assert.Empty(heading.Labels);

        // And it doesn't swell the count of what there is to do.
        Assert.StartsWith("1 task", presenter.Status);
    }

    [Fact]
    public void A_day_with_nothing_due_is_not_headed()
    {
        // Seven headings for two tasks would be a list mostly made of furniture.
        var presenter = Upcoming(
            Task("a", "Soon", "2026-08-01"),
            Task("b", "Later", "2026-08-06"));

        Assert.Equal(["1 Aug · Tomorrow", "Soon", "6 Aug · Thursday", "Later"], Contents(presenter));
    }

    [Fact]
    public void Every_other_view_is_left_as_one_list()
    {
        var presenter = Upcoming(Task("a", "Soon", "2026-08-01"));

        presenter.Select(ViewSelection.Of(SmartView.All));

        Assert.All(presenter.Rows, r => Assert.False(r.IsHeading));
    }

    [Fact]
    public void A_search_within_upcoming_drops_the_headings()
    {
        // A search crosses the whole account and answers with matches, not with days — and the
        // matches aren't all due in the next week to be grouped by.
        var presenter = Upcoming(
            Task("a", "Soon", "2026-08-01"),
            Task("b", "Later", "2026-08-06"));

        presenter.Search("so");

        Assert.Equal(["Soon"], Contents(presenter));
    }

    [Fact]
    public void A_sub_task_stands_under_its_own_day_rather_than_its_parent()
    {
        // It can't sit under both, and the day is what this view is for.
        var presenter = Upcoming(
            Task("a", "Parent", "2026-08-01"),
            Task("b", "Child", "2026-08-03", parent: "a"));

        Assert.Equal(["1 Aug · Tomorrow", "Parent", "3 Aug · Monday", "Child"], Contents(presenter));
        Assert.All(presenter.Rows, r => Assert.Equal(0, r.Depth));
    }

    [Fact]
    public void A_repeat_is_filed_under_the_day_it_next_falls_on()
    {
        // The row shows the words Todoist gave it — "every Monday" — which names no day at all, so
        // the day has to be read off the task rather than off what the column says.
        var presenter = Upcoming(
            Task("a", "Ordinary", "2026-08-03"),
            Repeat("b", "Weekly thing", "2026-08-03", "every Monday"));

        Assert.Equal(["3 Aug · Monday", "Ordinary", "Weekly thing"], Contents(presenter));
        Assert.Equal("every Monday", presenter.Rows[2].Due);
    }

    [Fact]
    public void Sorting_orders_each_day_and_leaves_the_days_where_they_are()
    {
        var presenter = Upcoming(
            Task("a", "Whenever", "2026-08-01", priority: 1),
            Task("b", "Urgent", "2026-08-01", priority: 4),
            Task("c", "Also urgent", "2026-08-02", priority: 4),
            Task("d", "Middling", "2026-08-02", priority: 2));

        presenter.SortBy(TaskColumn.Priority);

        Assert.Equal(
            ["1 Aug · Tomorrow", "Urgent", "Whenever", "2 Aug · Sunday", "Also urgent", "Middling"],
            Contents(presenter));
    }

    [Fact]
    public async Task A_finished_task_keeps_to_the_bottom_under_no_heading()
    {
        // It isn't work waiting on a day, so it stays where a finished task goes everywhere else.
        var presenter = Upcoming(Task("a", "Still to do", "2026-08-01"));

        await presenter.ToggleCompletedAsync();

        Assert.Equal(["1 Aug · Tomorrow", "Still to do"], Contents(presenter));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static string[] Contents(MainPresenter presenter)
        => presenter.Rows.Select(r => r.Content).ToArray();

    private static (string Id, string Json) Task(
        string id,
        string content,
        string due,
        int priority = 1,
        string? parent = null)
    {
        var fields = new List<string>
        {
            $"\"id\":\"{id}\"",
            $"\"content\":\"{content}\"",
            "\"project_id\":\"p\"",
            $"\"priority\":{priority}",
            $"\"due\":{{\"date\":\"{due}\"}}",
        };

        if (parent is not null) fields.Add($"\"parent_id\":\"{parent}\"");

        return (id, "{" + string.Join(",", fields) + "}");
    }

    /// <summary>A task Todoist describes in words, which name no day.</summary>
    private static (string Id, string Json) Repeat(string id, string content, string due, string said)
        => (id, "{"
            + $"\"id\":\"{id}\",\"content\":\"{content}\",\"project_id\":\"p\",\"priority\":1,"
            + $"\"due\":{{\"date\":\"{due}\",\"string\":\"{said}\",\"is_recurring\":true}}"
            + "}");

    /// <summary>
    /// A presenter on Upcoming, with the machine set to en-GB so the headings can be asserted.
    /// </summary>
    /// <remarks>
    /// A heading is Termyn's own words about a day rather than the server's wording, so it's
    /// written in whatever culture the machine is set to — which leaves it unassertable unless the
    /// culture is said.
    /// </remarks>
    /// <param name="tasks">What the account holds</param>
    /// <returns>The presenter, showing Upcoming</returns>
    private static MainPresenter Upcoming(params (string Id, string Json)[] tasks)
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-GB");

            var store = new InMemorySnapshotStore();
            store.PutResource("projects", "p", """{"id":"p","name":"Work"}""");
            foreach (var task in tasks)
                store.PutResource("items", task.Id, task.Json);

            var clock = new FixedClock(Today);
            var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, clock);
            engine.Load();

            var presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
            presenter.Select(ViewSelection.Of(SmartView.Upcoming));
            return presenter;
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}
