using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// What happens to a task in the moment after it's ticked off.
/// </summary>
/// <remarks>
/// Ticking one off is a single click on the box in its row, and with finished work hidden the row
/// it lands on would otherwise vanish — a slip takes a task off the screen and says nothing about
/// where it went. So it stays for a few seconds, drawn finished, with the box still under the
/// pointer to click again.
/// </remarks>
public class TickedOffTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>Longer than the stay, so a clock moved on by this has certainly outlasted it.</summary>
    private static readonly TimeSpan LongEnough = TimeSpan.FromSeconds(30);

    [Fact]
    public void A_task_ticked_off_stays_on_the_list_for_a_moment()
    {
        var (presenter, _) = Seeded();

        presenter.Complete("a");

        var row = presenter.Rows.Single(r => r.Id == "a");

        Assert.True(row.Completed);
        Assert.Equal("Ship it", row.Content);
    }

    [Fact]
    public void And_goes_once_it_has_been_there_long_enough()
    {
        var (presenter, clock) = Seeded();
        presenter.Complete("a");

        clock.Advance(LongEnough);

        Assert.True(presenter.DropTicked());
        Assert.DoesNotContain(presenter.Rows, r => r.Id == "a");
    }

    [Fact]
    public void Nothing_goes_before_its_time()
    {
        var (presenter, clock) = Seeded();
        presenter.Complete("a");

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(presenter.DropTicked());
        Assert.Contains(presenter.Rows, r => r.Id == "a");
    }

    [Fact]
    public void Putting_it_back_within_the_moment_leaves_it_where_it_was()
    {
        // The whole point of the stay: the box is still under the pointer, and clicking it again
        // undoes the slip without anybody having to know what Ctrl+Z does.
        var (presenter, clock) = Seeded();
        presenter.Complete("a");

        presenter.Reopen("a");

        Assert.False(presenter.Rows.Single(r => r.Id == "a").Completed);

        // And it isn't waiting to go any more, so the list doesn't lose it a moment later.
        clock.Advance(LongEnough);
        presenter.DropTicked();

        Assert.Contains(presenter.Rows, r => r.Id == "a");
    }

    [Fact]
    public void Once_it_is_back_the_status_line_stops_offering_to_put_it_back()
    {
        // Nothing is waiting to go any more, so a line naming Ctrl+Z would be about nothing — and
        // pressing it would take back the reopen rather than the tick.
        var (presenter, _) = Seeded();
        presenter.Complete("a");

        presenter.Reopen("a");

        Assert.False(presenter.Lingering);
        Assert.DoesNotContain("Ctrl+Z", presenter.Status);
    }

    [Fact]
    public void Taking_the_change_back_leaves_nothing_waiting_to_go()
    {
        var (presenter, clock) = Seeded();
        presenter.Complete("a");

        Assert.True(presenter.Undo());
        Assert.False(presenter.Lingering);

        clock.Advance(LongEnough);
        presenter.DropTicked();

        Assert.Contains(presenter.Rows, r => r.Id == "a");
    }

    [Fact]
    public async Task With_finished_work_on_show_the_row_is_there_once_and_not_twice()
    {
        // It's on the list because it's finished, not because it was just ticked off — and a task
        // counted by both would appear twice.
        var (presenter, _) = Seeded();
        await presenter.ToggleCompletedAsync();

        presenter.Complete("a");

        Assert.Single(presenter.Rows, r => r.Id == "a");
    }

    [Fact]
    public void The_status_line_names_the_way_back_while_the_row_is_still_there()
    {
        // Said where the app says everything else, at the one moment it's any use.
        var (presenter, clock) = Seeded();

        Assert.DoesNotContain("Ctrl+Z", presenter.Status);

        presenter.Complete("a");
        Assert.Contains("ticked off · Ctrl+Z puts it back", presenter.Status);

        clock.Advance(LongEnough);
        presenter.DropTicked();

        Assert.DoesNotContain("Ctrl+Z", presenter.Status);
    }

    [Fact]
    public void The_window_is_told_when_there_is_something_to_wait_for()
    {
        // What the window hangs its timer off, so an idle one ticks for nothing.
        var (presenter, clock) = Seeded();

        Assert.Null(presenter.LingerEnds);

        presenter.Complete("a");
        Assert.NotNull(presenter.LingerEnds);

        clock.Advance(LongEnough);
        presenter.DropTicked();

        Assert.Null(presenter.LingerEnds);
    }

    // ---- Where it stays --------------------------------------------------------------------------

    [Fact]
    public void In_Upcoming_it_stays_under_its_day()
    {
        // A finished task goes to the bottom of Upcoming, under no day. One only just ticked off
        // moving there straight away would slide the next task up under the pointer, and a second
        // click meant to put the first back would tick that one off instead.
        var (presenter, _) = Seeded(due: "2026-08-01");
        presenter.Select(ViewSelection.Of(SmartView.Upcoming));
        var before = presenter.Rows.Select(r => r.Id).ToList();

        presenter.Complete("a");

        Assert.Equal(before, presenter.Rows.Select(r => r.Id).ToList());
        Assert.True(presenter.Rows.Single(r => r.Id == "a").Completed);
    }

    [Fact]
    public void Sorted_by_a_column_it_keeps_its_place()
    {
        var (presenter, _) = Seeded();
        presenter.SortBy(TaskColumn.Content);
        var before = presenter.Rows.Select(r => r.Id).ToList();

        presenter.Complete("a");

        Assert.Equal(before, presenter.Rows.Select(r => r.Id).ToList());
    }

    [Fact]
    public async Task With_finished_work_on_show_it_stays_where_it_was_and_then_joins_it()
    {
        var (presenter, clock) = Seeded();
        await presenter.ToggleCompletedAsync();
        var before = presenter.Rows.Select(r => r.Id).ToList();

        presenter.Complete("a");

        Assert.Equal(before, presenter.Rows.Select(r => r.Id).ToList());

        clock.Advance(LongEnough);
        presenter.DropTicked();

        // Below the tasks still to do, where finished ones are listed.
        Assert.Equal(["b", "a"], presenter.Rows.Select(r => r.Id).ToList());
    }

    // ---- What it says and counts ---------------------------------------------------------------

    [Fact]
    public void A_recurring_task_isnt_kept_or_offered_back()
    {
        // It moves on to its next date rather than being ticked off, so there's no finished row to
        // keep — and once the close has gone, Ctrl+Z can't put the date back.
        var (presenter, _) = Seeded(recurring: true);

        presenter.Complete("a");

        Assert.False(presenter.Lingering);
        Assert.DoesNotContain("Ctrl+Z", presenter.Status);
    }

    [Fact]
    public void Taking_back_something_else_leaves_it_its_moment()
    {
        var (presenter, _) = Seeded();
        presenter.Complete("a");
        presenter.Delete("b");

        Assert.True(presenter.Undo());

        Assert.True(presenter.Rows.Single(r => r.Id == "a").Completed);
        Assert.True(presenter.Lingering);
    }

    [Fact]
    public void The_counts_drop_straight_away()
    {
        // A count is what's left to do, and a task ticked off isn't, however long its row stays.
        var (presenter, _) = Seeded(due: "2026-07-31");
        Assert.Equal(2, presenter.DueToday);

        presenter.Complete("a");

        Assert.Equal(1, presenter.DueToday);
        Assert.Equal(1, presenter.Sidebar.Single(n => n.Kind == SidebarKind.SmartView && n.View == SmartView.Today).Count);
    }

    // ---- A task with sub-tasks -----------------------------------------------------------------

    [Fact]
    public void Its_sub_tasks_stay_under_it_drawn_finished()
    {
        // They're finished too — the server takes them with it — and going on their own would move
        // the rows under the pointer while the parent was still there to click.
        var (presenter, _) = Seeded(withSubTasks: true);

        presenter.Complete("a");

        var rows = presenter.Rows.ToDictionary(r => r.Id);
        Assert.Equal((0, true), (rows["a"].Depth, rows["a"].Completed));
        Assert.Equal((1, true), (rows["a1"].Depth, rows["a1"].Completed));
        Assert.Equal((2, true), (rows["a2"].Depth, rows["a2"].Completed));
    }

    [Fact]
    public void And_go_when_it_does_rather_than_turning_up_on_their_own()
    {
        // Left behind, a sub-task with no parent in view is listed at the top level — which looks
        // like the tick promoted it.
        var (presenter, clock) = Seeded(withSubTasks: true);
        presenter.Complete("a");

        clock.Advance(LongEnough);
        presenter.DropTicked();

        Assert.DoesNotContain(presenter.Rows, r => r.Id is "a" or "a1" or "a2");
        Assert.Contains(presenter.Rows, r => r.Id == "b");
    }

    [Fact]
    public void Putting_it_back_brings_its_sub_tasks_back_with_it()
    {
        var (presenter, _) = Seeded(withSubTasks: true);
        presenter.Complete("a");

        presenter.Reopen("a");

        var rows = presenter.Rows.ToDictionary(r => r.Id);
        Assert.Equal((1, false), (rows["a1"].Depth, rows["a1"].Completed));
        Assert.Equal((2, false), (rows["a2"].Depth, rows["a2"].Completed));
    }

    /// <summary>Two tasks in one project, "a" then "b", with the list showing all of them.</summary>
    /// <param name="withSubTasks">Whether "a" has a sub-task, with one of its own</param>
    /// <param name="due">A day both tasks are due, or null for none</param>
    /// <param name="recurring">Whether "a" repeats</param>
    private static (MainPresenter Presenter, FixedClock Clock) Seeded(bool withSubTasks = false, string? due = null, bool recurring = false)
    {
        var dated = due is null ? string.Empty : $$""","due":{"date":"{{due}}"}""";
        var repeats = recurring ? ""","due":{"date":"2026-07-31","is_recurring":true,"string":"every day"}""" : dated;

        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work"}""");
        store.PutResource("items", "a", $$"""{"id":"a","content":"Ship it","project_id":"p","child_order":1{{repeats}}}""");
        store.PutResource("items", "b", $$"""{"id":"b","content":"Something else","project_id":"p","child_order":2{{dated}}}""");

        if (withSubTasks)
        {
            store.PutResource("items", "a1", """{"id":"a1","content":"Pack it","project_id":"p","parent_id":"a","child_order":1}""");
            store.PutResource("items", "a2", """{"id":"a2","content":"Label it","project_id":"p","parent_id":"a1","child_order":1}""");
        }

        var clock = new FixedClock(Today);
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, clock);
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
        presenter.Select(ViewSelection.Of(SmartView.All));

        return (presenter, clock);
    }
}
