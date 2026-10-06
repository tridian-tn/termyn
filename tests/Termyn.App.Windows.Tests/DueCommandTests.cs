using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The window's half of setting a due date: what the dialog is opened on, and what's done with the
/// answer.
/// </summary>
/// <remarks>
/// The dialog is answered rather than shown: it's modal, and nothing here has a hand to click it.
/// The window runs on the real clock, so every day here names its own year.
/// </remarks>
public class DueCommandTests
{
    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"Ship it","project_id":"p1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Already due","project_id":"p1","child_order":2,"due":{"date":"2026-08-04T16:00:00","string":"4 Aug 16:00","is_recurring":false}}""");
        store.PutResource("items", "r", """{"id":"r","content":"Water plants","project_id":"p1","child_order":3,"due":{"date":"2026-08-03","string":"every monday","is_recurring":true}}""");
        return store;
    }

    [WinFormsFact]
    public void The_command_sets_the_due_date_the_dialog_came_back_with()
    {
        using var window = TestWindow.Build("due-command.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        window.AskForDue = (_, _) => "11 Aug 2026 09:30";

        Assert.True(window.RunOnTask(AppCommand.Due, "a"));
        Assert.Equal(new DateOnly(2026, 8, 11), presenter.Rows.Single(r => r.Id == "a").DueOn);
    }

    [WinFormsFact]
    public void The_dialog_opens_on_the_day_and_time_the_task_has()
    {
        // Written the way the box reads them back, rather than the server's "4 Aug 16:00", which
        // has no year and would read as next year's once this one's has gone.
        using var window = TestWindow.Build("due-opens.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        string? opened = null;
        window.AskForDue = (_, current) =>
        {
            opened = current;
            return null;
        };

        window.RunOnTask(AppCommand.Due, "b");

        Assert.Equal("4 Aug 2026 16:00", opened);
    }

    [WinFormsFact]
    public void A_repeat_opens_on_its_own_words()
    {
        using var window = TestWindow.Build("due-repeat.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        string? opened = null;
        window.AskForDue = (_, current) =>
        {
            opened = current;
            return null;
        };

        window.RunOnTask(AppCommand.Due, "r");

        Assert.Equal("every monday", opened);
    }

    [WinFormsFact]
    public void The_dialog_is_asked_about_the_task_the_command_names()
    {
        using var window = TestWindow.Build("due-about.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        TaskRow? asked = null;
        window.AskForDue = (row, _) =>
        {
            asked = row;
            return null;
        };

        window.RunOnTask(AppCommand.Due, "b");

        Assert.Equal("Already due", asked?.Content);
    }

    [WinFormsFact]
    public void A_cancelled_dialog_writes_nothing()
    {
        using var window = TestWindow.Build("due-cancel.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        window.AskForDue = (_, _) => null;

        Assert.False(window.RunOnTask(AppCommand.Due, "b"));
        Assert.Empty(presenter.History.Recent());
    }

    [WinFormsTheory]
    [InlineData("4 Aug 2026 16:00")]
    [InlineData("4 aug 2026 at 4pm")]
    public void Coming_back_with_the_moment_it_opened_on_writes_nothing(string answer)
    {
        // Opening the dialog to look and pressing OK is not a change, and neither is retyping the
        // same moment in other words. Writing anyway queues a command to Todoist, notes it in what
        // you've done, and sets a sync going.
        using var window = TestWindow.Build("due-noop.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        window.AskForDue = (_, _) => answer;

        Assert.False(window.RunOnTask(AppCommand.Due, "b"));
        Assert.Empty(presenter.History.Recent());
    }

    [WinFormsFact]
    public void Clearing_from_the_dialog_takes_the_due_date_off()
    {
        using var window = TestWindow.Build("due-clear.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        window.AskForDue = (_, _) => string.Empty;

        Assert.True(window.RunOnTask(AppCommand.Due, "b"));
        Assert.Null(presenter.Rows.Single(r => r.Id == "b").DueOn);
    }

    [WinFormsFact]
    public void Words_waiting_for_the_server_can_be_cleared_before_they_reach_it()
    {
        // "in three weeks" set on a task with no due date has no day until it syncs. The dialog
        // opened empty on it, so clearing answered with what it opened on and wrote nothing.
        using var window = TestWindow.Build("due-pending.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));
        presenter.SetDueFromText("a", "in three weeks");

        string? opened = null;
        window.AskForDue = (_, current) =>
        {
            opened = current;
            return string.Empty;
        };

        Assert.True(window.RunOnTask(AppCommand.Due, "a"));
        Assert.Equal("in three weeks", opened);
        Assert.Equal(string.Empty, presenter.Rows.Single(r => r.Id == "a").Due);
    }

    [WinFormsFact]
    public void A_task_the_view_no_longer_holds_is_left_alone()
    {
        using var window = TestWindow.Build("due-gone.json", Account(), out _, out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        var asked = false;
        window.AskForDue = (_, _) =>
        {
            asked = true;
            return "11 Aug 2026";
        };

        Assert.False(window.RunOnTask(AppCommand.Due, "vanished"));
        Assert.False(asked);
    }
}
