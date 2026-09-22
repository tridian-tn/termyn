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

    private static (MainPresenter Presenter, FixedClock Clock) Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work"}""");
        store.PutResource("items", "a", """{"id":"a","content":"Ship it","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Something else","project_id":"p","child_order":2}""");

        var clock = new FixedClock(Today);
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, clock);
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
        presenter.Select(ViewSelection.Of(SmartView.All));

        return (presenter, clock);
    }
}
