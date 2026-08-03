using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The menus print their shortcuts from the same table the key handlers match against, so these are
/// the tests that keep a printed shortcut and a working one the same thing.
/// </summary>
public class ShortcutTests
{
    [Fact]
    public void Every_action_on_a_task_has_a_shortcut_to_print()
    {
        // The task menu is meant to teach the keyboard. An action reachable only by mouse would
        // show a blank where the shortcut goes and teach nothing.
        var missing = Menus.Commands(Menus.TaskContext)
            .Where(c => MainForm.ShortcutFor(c).Length == 0)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_shortcut_a_menu_prints_is_one_some_surface_answers_to()
    {
        // Printed and bound become two different truths the moment they are allowed to part
        // company: this walks from every menu entry back to a keystroke and checks it lands.
        var printed = Menus.Commands(Menus.Bar)
            .Concat(Menus.Commands(Menus.TaskContext))
            .Distinct()
            .Where(c => MainForm.ShortcutFor(c).Length > 0);

        foreach (var command in printed)
        {
            var bound = MainForm.Shortcuts.Where(s => s.Command == command).ToList();

            Assert.NotEmpty(bound);
            Assert.Contains(bound, s => MainForm.ShortcutText(s.Keys) == MainForm.ShortcutFor(command));
            Assert.All(bound, s => Assert.Equal(command, MainForm.CommandFor(s.Keys, s.Scope)));
        }
    }

    [Theory]
    [InlineData(Keys.Space, AppCommand.ToggleComplete)]
    [InlineData(Keys.Control | Keys.Enter, AppCommand.ToggleComplete)]
    [InlineData(Keys.F2, AppCommand.Rename)]
    [InlineData(Keys.Control | Keys.D, AppCommand.Due)]
    [InlineData(Keys.Control | Keys.D1, AppCommand.Priority1)]
    [InlineData(Keys.Control | Keys.D4, AppCommand.Priority4)]
    [InlineData(Keys.Control | Keys.L, AppCommand.Labels)]
    [InlineData(Keys.Control | Keys.R, AppCommand.Reminders)]
    [InlineData(Keys.Tab, AppCommand.Indent)]
    [InlineData(Keys.Shift | Keys.Tab, AppCommand.Outdent)]
    [InlineData(Keys.Alt | Keys.Up, AppCommand.MoveUp)]
    [InlineData(Keys.Alt | Keys.Down, AppCommand.MoveDown)]
    [InlineData(Keys.Delete, AppCommand.Delete)]
    [InlineData(Keys.Control | Keys.Z, AppCommand.Undo)]
    public void The_outline_answers_to_the_keys_it_always_did(Keys keys, AppCommand expected)
        => Assert.Equal(expected, MainForm.CommandFor(keys, MainForm.Scope.Outline));

    [Theory]
    [InlineData(Keys.F2, AppCommand.RenameSelection)]
    [InlineData(Keys.Delete, AppCommand.DeleteSelection)]
    [InlineData(Keys.Control | Keys.Shift | Keys.F, AppCommand.ToggleFavourite)]
    public void The_sidebar_answers_to_its_own(Keys keys, AppCommand expected)
        => Assert.Equal(expected, MainForm.CommandFor(keys, MainForm.Scope.Sidebar));

    [Theory]
    [InlineData(Keys.Control | Keys.N, AppCommand.NewTask)]
    [InlineData(Keys.Insert, AppCommand.NewTask)]
    [InlineData(Keys.Control | Keys.Shift | Keys.N, AppCommand.NewProject)]
    [InlineData(Keys.F5, AppCommand.SyncNow)]
    [InlineData(Keys.Control | Keys.H, AppCommand.ToggleCompleted)]
    [InlineData(Keys.Control | Keys.F, AppCommand.Search)]
    [InlineData(Keys.Control | Keys.K, AppCommand.Palette)]
    [InlineData(Keys.Control | Keys.Up, AppCommand.PreviousView)]
    [InlineData(Keys.Control | Keys.Down, AppCommand.NextView)]
    [InlineData(Keys.Control | Keys.Oemcomma, AppCommand.Settings)]
    public void The_window_answers_to_the_ones_that_work_anywhere(Keys keys, AppCommand expected)
        => Assert.Equal(expected, MainForm.CommandFor(keys, MainForm.Scope.Window));

    [Fact]
    public void The_same_key_means_a_different_thing_in_each_list()
    {
        // F2 and Delete belong to both lists, and which one is meant is decided by where the user
        // is — not by one of the two winning outright.
        Assert.Equal(AppCommand.Rename, MainForm.CommandFor(Keys.F2, MainForm.Scope.Outline));
        Assert.Equal(AppCommand.RenameSelection, MainForm.CommandFor(Keys.F2, MainForm.Scope.Sidebar));

        Assert.Equal(AppCommand.Delete, MainForm.CommandFor(Keys.Delete, MainForm.Scope.Outline));
        Assert.Equal(AppCommand.DeleteSelection, MainForm.CommandFor(Keys.Delete, MainForm.Scope.Sidebar));

        // And neither is claimed window-wide, where it would fire whatever had the focus.
        Assert.Equal(AppCommand.None, MainForm.CommandFor(Keys.F2, MainForm.Scope.Window));
        Assert.Equal(AppCommand.None, MainForm.CommandFor(Keys.Delete, MainForm.Scope.Window));
    }

    [Fact]
    public void Undo_is_not_claimed_window_wide()
    {
        // Taken window-wide it would reach the capture and search boxes, where Ctrl+Z has to go on
        // undoing the word just typed rather than the last write to the account.
        Assert.Equal(AppCommand.None, MainForm.CommandFor(Keys.Control | Keys.Z, MainForm.Scope.Window));
        Assert.Equal(AppCommand.Undo, MainForm.CommandFor(Keys.Control | Keys.Z, MainForm.Scope.Outline));
    }

    [Fact]
    public void A_keystroke_that_means_nothing_there_asks_for_nothing()
    {
        // A Fact rather than a Theory of scopes: the scope is internal to the window, and a public
        // test method can't take one as a parameter.
        Assert.Equal(AppCommand.None, MainForm.CommandFor(Keys.A, MainForm.Scope.Outline));
        Assert.Equal(AppCommand.None, MainForm.CommandFor(Keys.Escape, MainForm.Scope.Window));

        // The palette belongs to the window, and due dates to the outline; neither answers from
        // where the other lives.
        Assert.Equal(AppCommand.None, MainForm.CommandFor(Keys.Control | Keys.K, MainForm.Scope.Outline));
        Assert.Equal(AppCommand.None, MainForm.CommandFor(Keys.Control | Keys.D, MainForm.Scope.Sidebar));
    }

    [Theory]
    [InlineData(Keys.Control | Keys.D1, "Ctrl+1")]
    [InlineData(Keys.Control | Keys.D4, "Ctrl+4")]
    [InlineData(Keys.Shift | Keys.Tab, "Shift+Tab")]
    [InlineData(Keys.Alt | Keys.Up, "Alt+↑")]
    [InlineData(Keys.Alt | Keys.Down, "Alt+↓")]
    [InlineData(Keys.Delete, "Del")]
    [InlineData(Keys.Space, "Space")]
    [InlineData(Keys.F2, "F2")]
    [InlineData(Keys.Control | Keys.Oemcomma, "Ctrl+,")]
    [InlineData(Keys.Control | Keys.Shift | Keys.N, "Ctrl+Shift+N")]
    [InlineData(Keys.Control | Keys.Enter, "Ctrl+Enter")]
    public void A_shortcut_is_written_the_way_a_menu_writes_it(Keys keys, string expected)
        => Assert.Equal(expected, MainForm.ShortcutText(keys));

    [Fact]
    public void The_shortcut_printed_for_completing_is_the_bare_key_not_the_second_binding()
    {
        // Space and Ctrl+Enter both tick a task off. A menu has room for one, and it should be the
        // one that is easier to reach.
        Assert.Equal("Space", MainForm.ShortcutFor(AppCommand.ToggleComplete));
    }

    [Fact]
    public void New_section_prints_no_shortcut_of_its_own()
    {
        // Ctrl+N reaches it, but only from the sidebar and only over a project — printed beside
        // New section it would read as a second, unconditional binding for the same keystroke that
        // New task already claims.
        Assert.Equal(string.Empty, MainForm.ShortcutFor(AppCommand.NewSection));
        Assert.Equal("Ctrl+N", MainForm.ShortcutFor(AppCommand.NewTask));
    }
}
