using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The window's half of setting a deadline: the command, the dialog it puts up, and what it does
/// with the answer.
/// </summary>
/// <remarks>
/// The middle of it, which neither end can see. The presenter can be asked to set a deadline and
/// the dialog can be asked what day it settled on, and both answer correctly whether or not the
/// window ever carries one to the other — so a command wired to the wrong prompt, or an answer
/// dropped on the floor, would pass every test on either side.
///
/// The dialog is answered rather than shown: it's modal, and nothing here has a hand to click it.
/// </remarks>
public class DeadlineCommandTests
{
    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"Ship it","project_id":"p1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Already dated","project_id":"p1","child_order":2,"deadline":{"date":"2026-08-04","lang":"en"}}""");
        return store;
    }

    [WinFormsFact]
    public void The_command_sets_the_day_the_dialog_came_back_with()
    {
        using var window = TestWindow.Build("deadline-command.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        window.AskForDeadline = _ => (true, new DateOnly(2026, 8, 11));

        Assert.True(window.RunOnTask(AppCommand.Deadline, "a"));
        Assert.Equal("11 Aug", presenter.Rows.Single(r => r.Id == "a").Deadline);
    }

    [WinFormsFact]
    public void The_dialog_is_asked_about_the_task_the_command_names()
    {
        // Not the one the outline happens to be on: a dialog headed with one task while writing to
        // another is the kind of wrong nobody notices until it has been done a few times.
        using var window = TestWindow.Build("deadline-about.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        TaskRow? asked = null;
        window.AskForDeadline = row =>
        {
            asked = row;
            return (false, null);
        };

        window.RunOnTask(AppCommand.Deadline, "b");

        Assert.Equal("Already dated", asked?.Content);
        Assert.Equal(new DateOnly(2026, 8, 4), asked?.DeadlineOn);
    }

    [WinFormsFact]
    public void A_cancelled_dialog_writes_nothing()
    {
        using var window = TestWindow.Build("deadline-cancel.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        window.AskForDeadline = _ => (false, null);

        Assert.False(window.RunOnTask(AppCommand.Deadline, "b"));
        Assert.Equal("4 Aug", presenter.Rows.Single(r => r.Id == "b").Deadline);
    }

    [WinFormsFact]
    public void Coming_back_with_the_day_it_opened_on_writes_nothing()
    {
        // Opening the dialog to look at a deadline and pressing OK is not a change. Writing anyway
        // queues a command to Todoist, notes it in what you've done, and sets a sync going.
        using var window = TestWindow.Build("deadline-noop.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        window.AskForDeadline = row => (true, row.DeadlineOn);

        Assert.False(window.RunOnTask(AppCommand.Deadline, "b"));
        Assert.Empty(presenter.History.Recent());
    }

    [WinFormsFact]
    public void Clearing_from_the_dialog_takes_the_deadline_off()
    {
        using var window = TestWindow.Build("deadline-clear.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        window.AskForDeadline = _ => (true, null);

        Assert.True(window.RunOnTask(AppCommand.Deadline, "b"));
        Assert.Equal(string.Empty, presenter.Rows.Single(r => r.Id == "b").Deadline);
    }

    [WinFormsFact]
    public void A_task_the_view_no_longer_holds_is_left_alone()
    {
        using var window = TestWindow.Build("deadline-gone.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        var asked = false;
        window.AskForDeadline = _ =>
        {
            asked = true;
            return (true, new DateOnly(2026, 8, 11));
        };

        Assert.False(window.RunOnTask(AppCommand.Deadline, "vanished"));
        Assert.False(asked);
    }
}
