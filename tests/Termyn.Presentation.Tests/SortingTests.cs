using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>Ordering the outline by a column, and getting back to the account's own order.</summary>
public class SortingTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- What a click asks for -----------------------------------------------------------------

    [Fact]
    public void A_first_click_orders_by_that_column_from_the_top()
        => Assert.Equal(new TaskSort(TaskColumn.Due), TaskSort.Default.Clicked(TaskColumn.Due));

    [Fact]
    public void Clicking_the_same_column_again_turns_it_round()
    {
        var once = TaskSort.Default.Clicked(TaskColumn.Due);
        var twice = once.Clicked(TaskColumn.Due);

        Assert.True(twice.Descending);
        Assert.Equal(TaskColumn.Due, twice.Column);

        // And a third time comes back up rather than falling out of the sort altogether: nothing
        // else on Windows undoes a sort on the third click, and the menu is the way back.
        Assert.Equal(once, twice.Clicked(TaskColumn.Due));
    }

    [Fact]
    public void Clicking_a_different_column_starts_at_the_top_of_that_one()
    {
        var descending = new TaskSort(TaskColumn.Due, Descending: true);

        Assert.Equal(new TaskSort(TaskColumn.Project), descending.Clicked(TaskColumn.Project));
    }

    // ---- The order it produces -----------------------------------------------------------------

    [Fact]
    public void The_outline_starts_in_the_account_order()
    {
        var presenter = Seeded();

        Assert.True(presenter.Sort.IsDefault);
        Assert.Equal(["Beta", "Zulu sub", "Alpha sub", "Alpha"], Contents(presenter));
    }

    [Fact]
    public void Sorting_orders_the_top_level_and_each_set_of_sub_tasks_within_it()
    {
        var presenter = Seeded();

        presenter.SortBy(TaskColumn.Content);

        // Alpha before Beta at the top; Beta's own two in order beneath it, not ranked against the
        // whole list — "Alpha sub" sorts first of everything by name and still belongs to Beta.
        Assert.Equal(["Alpha", "Beta", "Alpha sub", "Zulu sub"], Contents(presenter));
    }

    [Fact]
    public void A_sort_leaves_the_nesting_where_it_was()
    {
        var presenter = Seeded();

        presenter.SortBy(TaskColumn.Content);

        Assert.Equal([0, 0, 1, 1], presenter.Rows.Select(r => r.Depth).ToArray());
    }

    [Fact]
    public void A_sub_task_stays_under_its_own_parent_however_the_column_points()
    {
        var presenter = Seeded();

        presenter.SortBy(TaskColumn.Content);
        presenter.SortBy(TaskColumn.Content);

        // Reversed at both levels, and the two sub-tasks still follow Beta rather than Alpha.
        Assert.Equal(["Beta", "Zulu sub", "Alpha sub", "Alpha"], Contents(presenter));
        Assert.Equal([0, 1, 1, 0], presenter.Rows.Select(r => r.Depth).ToArray());
    }

    [Fact]
    public void Sorting_by_priority_leads_with_the_most_urgent()
    {
        var presenter = new Fixture()
            .Task("a", "Middling", priority: 2)   // API 2 is P3
            .Task("b", "Urgent", priority: 4)     // API 4 is P1
            .Task("c", "Whenever", priority: 1)   // API 1 is P4
            .Presenter();

        presenter.SortBy(TaskColumn.Priority);

        Assert.Equal(["Urgent", "Middling", "Whenever"], Contents(presenter));
    }

    [Fact]
    public void Sorting_by_due_date_uses_the_day_it_falls_on_not_the_words_it_shows()
    {
        // A repeat reads "every Monday" in the column, which sorts nowhere near a Monday among a
        // list of dates. The day behind it is what the order is built on.
        var presenter = new Fixture()
            .Task("a", "Later", due: """{"date":"2026-09-01"}""")
            .Task("b", "Repeating", due: """{"date":"2026-08-01","string":"every Monday","is_recurring":true}""")
            .Task("c", "Soonest", due: """{"date":"2026-07-31"}""")
            .Presenter();

        presenter.SortBy(TaskColumn.Due);

        Assert.Equal(["Soonest", "Repeating", "Later"], Contents(presenter));
    }

    [Fact]
    public void An_undated_task_sits_at_the_bottom_whichever_way_the_column_points()
    {
        var presenter = new Fixture()
            .Task("a", "Undated")
            .Task("b", "Earlier", due: """{"date":"2026-07-31"}""")
            .Task("c", "Later", due: """{"date":"2026-09-01"}""")
            .Presenter();

        presenter.SortBy(TaskColumn.Due);
        Assert.Equal(["Earlier", "Later", "Undated"], Contents(presenter));

        // Undated is not "furthest away" — it isn't on the calendar at all, so turning the column
        // round must not lift it to the top.
        presenter.SortBy(TaskColumn.Due);
        Assert.Equal(["Later", "Earlier", "Undated"], Contents(presenter));
    }

    [Fact]
    public void A_task_with_no_labels_sits_at_the_bottom_of_a_label_sort()
    {
        var presenter = new Fixture()
            .Task("a", "Bare")
            .Task("b", "Tagged", labels: """["zebra"]""")
            .Presenter();

        presenter.SortBy(TaskColumn.Labels);
        Assert.Equal(["Tagged", "Bare"], Contents(presenter));

        presenter.SortBy(TaskColumn.Labels);
        Assert.Equal(["Tagged", "Bare"], Contents(presenter));
    }

    [Fact]
    public async Task Completed_tasks_stay_below_the_outstanding_ones()
    {
        // Sorting by priority is not a request to have last month's finished work back among this
        // week's, so the two groups are ordered but not mixed.
        var done = Json.Change(
            "items",
            "done",
            """{"id":"done","content":"Urgent and done","project_id":"p","priority":4,"checked":true,"completed_at":"2026-07-30T09:00:00Z"}""");

        var api = new FakeApi
        {
            Response = new SyncResponse { SyncToken = "s1" },
            Completed = _ => new CompletedPage([done], null),
        };

        var presenter = new Fixture().Task("a", "Whenever", priority: 1).Presenter(api);
        await presenter.ToggleCompletedAsync();

        presenter.SortBy(TaskColumn.Priority);

        Assert.Equal(["Whenever", "Urgent and done"], Contents(presenter));
    }

    [Fact]
    public void Ties_come_out_the_same_way_twice()
    {
        // Everything in one project, so the column being sorted on says nothing about the order.
        var presenter = new Fixture()
            .Task("c", "Third")
            .Task("a", "First")
            .Task("b", "Second")
            .Presenter();

        presenter.SortBy(TaskColumn.Project);
        var once = Contents(presenter);

        presenter.SortBy(TaskColumn.Content);
        presenter.SortBy(TaskColumn.Project);

        Assert.Equal(once, Contents(presenter));
    }

    // ---- Getting back --------------------------------------------------------------------------

    [Fact]
    public void Clearing_the_sort_puts_the_tree_back_as_it_was()
    {
        var presenter = Seeded();
        var original = Contents(presenter);
        var depths = presenter.Rows.Select(r => r.Depth).ToArray();

        presenter.SortBy(TaskColumn.Content);
        Assert.True(presenter.ClearSort());

        Assert.Equal(original, Contents(presenter));
        Assert.Equal(depths, presenter.Rows.Select(r => r.Depth).ToArray());
        Assert.True(presenter.Sort.IsDefault);
    }

    [Fact]
    public void Clearing_a_sort_that_was_never_applied_changes_nothing()
        => Assert.False(Seeded().ClearSort());

    [Fact]
    public void A_sort_survives_a_sync_bringing_new_rows()
    {
        // The order is the user's, not the publish's: a background sync every 45 seconds must not
        // quietly put the outline back the way the account has it.
        var presenter = Seeded();
        presenter.SortBy(TaskColumn.Content);

        presenter.AddProject("Anything, to force a publish");

        Assert.Equal(TaskColumn.Content, presenter.Sort.Column);
        Assert.Equal(["Alpha", "Beta", "Alpha sub", "Zulu sub"], Contents(presenter));
    }

    [Fact]
    public void A_search_within_a_sorted_outline_stays_sorted()
    {
        var presenter = Seeded();
        presenter.SortBy(TaskColumn.Content);

        presenter.Search("a");

        Assert.Equal(Contents(presenter).OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase), Contents(presenter));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static string[] Contents(MainPresenter presenter)
        => presenter.Rows.Select(r => r.Content).ToArray();

    /// <summary>
    /// Tasks in one project, in the order they are written here — which is the account's own order,
    /// and so the one every sort has to be visibly different from.
    /// </summary>
    private sealed class Fixture
    {
        private readonly InMemorySnapshotStore _store = new();
        private int _order;

        public Fixture Task(
            string id,
            string content,
            int priority = 1,
            string? due = null,
            string? labels = null,
            string? parent = null)
        {
            var fields = new List<string>
            {
                $"\"id\":\"{id}\"",
                $"\"content\":\"{content}\"",
                "\"project_id\":\"p\"",
                $"\"priority\":{priority}",
                $"\"child_order\":{++_order}",
            };

            if (due is not null) fields.Add($"\"due\":{due}");
            if (labels is not null) fields.Add($"\"labels\":{labels}");
            if (parent is not null) fields.Add($"\"parent_id\":\"{parent}\"");

            _store.PutResource("items", id, "{" + string.Join(",", fields) + "}");
            return this;
        }

        /// <summary>A presenter over these tasks, showing all of them.</summary>
        public MainPresenter Presenter(FakeApi? api = null)
        {
            var engine = new SyncEngine(api ?? new FakeApi(), _store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
            engine.Load();

            var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
            presenter.Select(ViewSelection.Of(SmartView.All));
            return presenter;
        }
    }

    /// <summary>
    /// Beta with two sub-tasks, then Alpha. Both levels are out of alphabetical order to begin
    /// with, so a sort that only reached the top level would be caught by it.
    /// </summary>
    private static MainPresenter Seeded() => new Fixture()
        .Task("b", "Beta")
        .Task("b2", "Zulu sub", parent: "b")
        .Task("b1", "Alpha sub", parent: "b")
        .Task("a", "Alpha")
        .Presenter();
}
