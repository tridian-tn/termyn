using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The menu prints its shortcuts from the same table the outline matches keystrokes against, so
/// these are the tests that keep a printed shortcut and a working one the same thing.
/// </summary>
public class TaskShortcutTests
{
    private static readonly TaskRow Active = new("i1", "Write it up", Priority.P3, "Work", string.Empty, []);

    [Fact]
    public void Every_action_in_the_menu_has_a_shortcut_to_print()
    {
        // The whole point of the menu is that it teaches the keyboard. An action reachable only by
        // mouse would show a blank where the shortcut goes and teach nothing.
        var missing = TaskMenu.Commands(TaskMenu.For(Active))
            .Where(c => MainForm.ShortcutFor(c).Length == 0)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_shortcut_the_menu_prints_is_one_the_outline_answers_to()
    {
        // Printed and bound are two different tables' worth of truth if they are ever allowed to
        // part company: this walks from the menu back to the keystroke and checks it lands.
        foreach (var command in TaskMenu.Commands(TaskMenu.For(Active)))
        {
            var printed = MainForm.ShortcutFor(command);
            var bound = MainForm.TaskShortcuts.Where(s => s.Command == command).ToList();

            Assert.NotEmpty(bound);
            Assert.Contains(bound, s => MainForm.ShortcutText(s.Keys) == printed);
            Assert.All(bound, s => Assert.Equal(command, MainForm.CommandFor(s.Keys)));
        }
    }

    [Theory]
    [InlineData(Keys.Space, TaskCommand.ToggleComplete)]
    [InlineData(Keys.Control | Keys.Enter, TaskCommand.ToggleComplete)]
    [InlineData(Keys.F2, TaskCommand.Rename)]
    [InlineData(Keys.Control | Keys.D, TaskCommand.Due)]
    [InlineData(Keys.Control | Keys.D1, TaskCommand.Priority1)]
    [InlineData(Keys.Control | Keys.D4, TaskCommand.Priority4)]
    [InlineData(Keys.Control | Keys.L, TaskCommand.Labels)]
    [InlineData(Keys.Control | Keys.R, TaskCommand.Reminders)]
    [InlineData(Keys.Tab, TaskCommand.Indent)]
    [InlineData(Keys.Shift | Keys.Tab, TaskCommand.Outdent)]
    [InlineData(Keys.Alt | Keys.Up, TaskCommand.MoveUp)]
    [InlineData(Keys.Alt | Keys.Down, TaskCommand.MoveDown)]
    [InlineData(Keys.Delete, TaskCommand.Delete)]
    public void The_keys_the_outline_answered_to_before_still_reach_the_same_action(Keys keys, TaskCommand expected)
        => Assert.Equal(expected, MainForm.CommandFor(keys));

    [Theory]
    [InlineData(Keys.A)]
    [InlineData(Keys.Escape)]
    [InlineData(Keys.Control | Keys.Z)]   // undo isn't an action on a task
    [InlineData(Keys.F5)]                 // nor is a sync
    [InlineData(Keys.Control | Keys.K)]   // the palette belongs to the window
    public void A_keystroke_that_is_no_task_action_asks_for_nothing(Keys keys)
        => Assert.Equal(TaskCommand.None, MainForm.CommandFor(keys));

    [Theory]
    [InlineData(Keys.Control | Keys.D1, "Ctrl+1")]
    [InlineData(Keys.Control | Keys.D4, "Ctrl+4")]
    [InlineData(Keys.Shift | Keys.Tab, "Shift+Tab")]
    [InlineData(Keys.Alt | Keys.Up, "Alt+↑")]
    [InlineData(Keys.Alt | Keys.Down, "Alt+↓")]
    [InlineData(Keys.Delete, "Del")]
    [InlineData(Keys.Space, "Space")]
    [InlineData(Keys.F2, "F2")]
    [InlineData(Keys.Control | Keys.Enter, "Ctrl+Enter")]
    public void A_shortcut_is_written_the_way_a_menu_writes_it(Keys keys, string expected)
        => Assert.Equal(expected, MainForm.ShortcutText(keys));

    [Fact]
    public void The_shortcut_printed_for_completing_is_the_bare_key_not_the_second_binding()
    {
        // Space and Ctrl+Enter both tick a task off. A menu has room for one, and it should be the
        // one that is easier to reach.
        Assert.Equal("Space", MainForm.ShortcutFor(TaskCommand.ToggleComplete));
    }
}
