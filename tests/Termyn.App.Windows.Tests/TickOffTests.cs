using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The window's half of the box on a row: which way a click goes, and the timer that clears up
/// after it.
/// </summary>
/// <remarks>
/// The middle of it, which neither end can see. The list says which box was clicked and the
/// presenter ticks off or reopens whatever it's told to, and both answer correctly whether or not
/// the window asks the right one — so a click that reopened an open task, or a timer that never
/// started, would pass every test on either side.
/// </remarks>
public class TickOffTests
{
    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"Ship it","project_id":"p1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Something else","project_id":"p1","child_order":2}""");
        return store;
    }

    [WinFormsFact]
    public void Clicking_an_open_tasks_box_ticks_it_off()
    {
        using var window = TestWindow.Build("tick-off.json", Account(), out _, out var presenter);
        _ = window.Handle;
        presenter.Select(ViewSelection.Of(SmartView.All));

        ClickBoxOf(window, "a");

        Assert.True(presenter.Rows.Single(r => r.Id == "a").Completed);
        Assert.False(presenter.Rows.Single(r => r.Id == "b").Completed);
    }

    [WinFormsFact]
    public void Clicking_it_again_puts_it_back()
    {
        using var window = TestWindow.Build("tick-back.json", Account(), out _, out var presenter);
        _ = window.Handle;
        presenter.Select(ViewSelection.Of(SmartView.All));

        ClickBoxOf(window, "a");
        ClickBoxOf(window, "a");

        Assert.False(presenter.Rows.Single(r => r.Id == "a").Completed);
    }

    [WinFormsFact]
    public void The_timer_only_runs_while_something_ticked_off_is_still_showing()
    {
        using var window = TestWindow.Build("tick-timer.json", Account(), out _, out var presenter);
        _ = window.Handle;
        presenter.Select(ViewSelection.Of(SmartView.All));

        Assert.False(window.DroppingTicked);

        ClickBoxOf(window, "a");
        Assert.True(window.DroppingTicked);

        // Put back, there's nothing left for it to take away.
        ClickBoxOf(window, "a");
        Assert.False(window.DroppingTicked);
    }

    /// <summary>Clicks a task's box the way a mouse would, finding it by asking the list.</summary>
    private static void ClickBoxOf(MainForm window, string id)
    {
        var outline = TestWindow.Find<OutlineView>(window);
        var index = outline.Rows.ToList().FindIndex(r => r.Id == id);
        var row = outline.GetItemRect(index);

        for (var x = row.Left; x < row.Left + 200; x++)
        {
            var at = new Point(x, row.Top + (row.Height / 2));

            if (outline.CheckboxAt(at) == id)
            {
                RealMouse.Click(outline, at);
                return;
            }
        }

        Assert.Fail($"no box found on '{id}'s row");
    }
}
