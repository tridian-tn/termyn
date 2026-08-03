using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

public class TaskMenuTests
{
    private static TaskRow Row(Priority priority = Priority.P4, bool completed = false)
        => new("i1", "Write it up", priority, "Work", string.Empty, [], Completed: completed);

    [Fact]
    public void Every_action_the_outline_takes_on_a_task_is_in_the_menu()
    {
        // The menu is meant to be the whole of what can be done to a task, so that someone who
        // never learns a keystroke is not shut out of half the app.
        Assert.Equal(
            [
                TaskCommand.ToggleComplete,
                TaskCommand.Rename,
                TaskCommand.Due,
                TaskCommand.Priority1,
                TaskCommand.Priority2,
                TaskCommand.Priority3,
                TaskCommand.Priority4,
                TaskCommand.Labels,
                TaskCommand.Reminders,
                TaskCommand.Indent,
                TaskCommand.Outdent,
                TaskCommand.MoveUp,
                TaskCommand.MoveDown,
                TaskCommand.Delete,
            ],
            TaskMenu.Commands(TaskMenu.For(Row())).ToArray());
    }

    [Fact]
    public void A_task_still_to_do_is_offered_completion()
        => Assert.Equal("Complete", Top(Row()).First(e => e.Command == TaskCommand.ToggleComplete).Label);

    [Fact]
    public void A_task_already_done_is_offered_reopening_instead()
    {
        // The same action and the same keystroke; only what it is called changes, because on a
        // completed row "Complete" would describe the state rather than the offer.
        Assert.Equal("Reopen", Top(Row(completed: true)).First(e => e.Command == TaskCommand.ToggleComplete).Label);
    }

    [Fact]
    public void The_priority_the_task_is_on_is_the_one_ticked()
    {
        var priorities = Priorities(Row(Priority.P2));

        Assert.Equal(TaskCommand.Priority2, priorities.Single(e => e.Checked).Command);
    }

    [Fact]
    public void Every_priority_is_offered_including_the_one_that_clears_it()
    {
        var priorities = Priorities(Row(Priority.P1));

        Assert.Equal(4, priorities.Count);
        Assert.Single(priorities, e => e.Checked);

        // P4 is Todoist's "no priority", so it has to be reachable or a flag set by accident could
        // never be taken off again from here.
        Assert.Contains(priorities, e => e.Command == TaskCommand.Priority4);
    }

    [Fact]
    public void The_heading_that_holds_the_priorities_is_not_itself_an_action()
    {
        // It only opens a submenu. Left as a real command it would run something when clicked.
        var heading = Top(Row()).Single(e => e.Children is { Count: > 0 });

        Assert.Equal(TaskCommand.None, heading.Command);
    }

    [Fact]
    public void Nothing_in_the_menu_is_both_a_heading_and_an_action()
    {
        Assert.All(
            Top(Row()),
            e => Assert.True(e.Command == TaskCommand.None ^ e.Children is null or { Count: 0 }));
    }

    [Fact]
    public void Every_entry_is_labelled()
        => Assert.All(TaskMenu.For(Row()), e => Assert.False(string.IsNullOrWhiteSpace(e.Label)));

    [Fact]
    public void The_menu_does_not_open_on_a_separator()
    {
        // A leading separator draws a rule against the top edge of the menu.
        Assert.False(TaskMenu.For(Row())[0].SeparatorBefore);
    }

    [Fact]
    public void Deleting_is_kept_apart_from_whatever_is_above_it()
    {
        // The one action here that can't be taken back by pressing the same key again, so it does
        // not sit flush against the thing above it in the list.
        Assert.True(Top(Row()).Single(e => e.Command == TaskCommand.Delete).SeparatorBefore);
    }

    [Fact]
    public void What_the_menu_offers_does_not_depend_on_the_priority_the_task_is_on()
    {
        // Only the tick moves. A menu that grew or shrank with the task's priority would be a menu
        // whose items are in a different place each time it is opened.
        Assert.Equal(
            TaskMenu.Commands(TaskMenu.For(Row(Priority.P1))).ToArray(),
            TaskMenu.Commands(TaskMenu.For(Row(Priority.P4))).ToArray());
    }

    private static IReadOnlyList<TaskMenuEntry> Top(TaskRow row) => TaskMenu.For(row);

    private static IReadOnlyList<TaskMenuEntry> Priorities(TaskRow row)
        => Top(row).Single(e => e.Children is { Count: > 0 }).Children!;
}
