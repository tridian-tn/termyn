using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The menus print their shortcuts from the same table the key handlers match against, so these are
/// the tests that keep a printed shortcut and a working one the same thing.
/// </summary>
/// <remarks>
/// Written in portable keystrokes. How WinForms' own key codes become these is the Windows
/// project's half, and tested there in <c>KeyMapTests</c>.
/// </remarks>
public class ShortcutTests
{
    private const HotkeyModifiers None = HotkeyModifiers.None;
    private const HotkeyModifiers Ctrl = HotkeyModifiers.Control;
    private const HotkeyModifiers Shift = HotkeyModifiers.Shift;
    private const HotkeyModifiers Alt = HotkeyModifiers.Alt;

    private static AppCommand In(ShortcutScope scope, HotkeyModifiers modifiers, Key key)
        => Shortcuts.CommandFor(new Keystroke(modifiers, key), scope);

    [Fact]
    public void Every_action_on_a_task_has_a_shortcut_to_print()
    {
        // The task menu is meant to teach the keyboard. An action reachable only by mouse would
        // show a blank where the shortcut goes and teach nothing.
        var missing = Menus.Commands(Menus.TaskContext)
            .Where(c => Shortcuts.ShortcutFor(c).Length == 0)
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
            .Where(c => Shortcuts.ShortcutFor(c).Length > 0);

        foreach (var command in printed)
        {
            var bound = Shortcuts.All.Where(s => s.Command == command).ToList();

            Assert.NotEmpty(bound);
            Assert.Contains(bound, s => s.Keystroke.ToString() == Shortcuts.ShortcutFor(command));
            Assert.All(bound, s => Assert.Equal(command, Shortcuts.CommandFor(s.Keystroke, s.Scope)));
        }
    }

    [Theory]
    [InlineData(None, Key.Space, AppCommand.ToggleComplete)]
    [InlineData(Ctrl, Key.Enter, AppCommand.ToggleComplete)]
    [InlineData(None, Key.F2, AppCommand.Rename)]
    [InlineData(Ctrl, Key.D, AppCommand.Due)]
    [InlineData(Ctrl | Shift, Key.D, AppCommand.Deadline)]
    [InlineData(Ctrl, Key.Digit1, AppCommand.Priority1)]
    [InlineData(Ctrl, Key.Digit4, AppCommand.Priority4)]
    [InlineData(Ctrl, Key.L, AppCommand.Labels)]
    [InlineData(Ctrl, Key.R, AppCommand.Reminders)]
    [InlineData(Ctrl, Key.Right, AppCommand.Indent)]
    [InlineData(Ctrl, Key.Left, AppCommand.Outdent)]
    [InlineData(Ctrl, Key.Up, AppCommand.MoveUp)]
    [InlineData(Ctrl, Key.Down, AppCommand.MoveDown)]
    [InlineData(Ctrl, Key.M, AppCommand.MoveTo)]
    [InlineData(Ctrl, Key.O, AppCommand.ShowInTodoist)]
    [InlineData(None, Key.Delete, AppCommand.Delete)]
    [InlineData(Ctrl, Key.Z, AppCommand.Undo)]
    public void The_outline_answers_to_its_own_keys(HotkeyModifiers modifiers, Key key, AppCommand expected)
        => Assert.Equal(expected, In(ShortcutScope.Outline, modifiers, key));

    [Theory]
    [InlineData(None, Key.F2, AppCommand.RenameSelection)]
    [InlineData(None, Key.Delete, AppCommand.DeleteSelection)]
    [InlineData(Ctrl | Shift, Key.F, AppCommand.ToggleFavourite)]
    [InlineData(Ctrl | Shift, Key.Up, AppCommand.MoveSelectionUp)]
    [InlineData(Ctrl | Shift, Key.Down, AppCommand.MoveSelectionDown)]
    public void The_sidebar_answers_to_its_own(HotkeyModifiers modifiers, Key key, AppCommand expected)
        => Assert.Equal(expected, In(ShortcutScope.Sidebar, modifiers, key));

    [Theory]
    [InlineData(Ctrl, Key.N, AppCommand.NewTask)]
    [InlineData(None, Key.Insert, AppCommand.NewTask)]
    [InlineData(Ctrl | Alt | Shift, Key.N, AppCommand.NewProject)]
    [InlineData(None, Key.F5, AppCommand.SyncNow)]
    [InlineData(Ctrl, Key.H, AppCommand.ToggleCompleted)]
    [InlineData(Ctrl, Key.F, AppCommand.Search)]
    [InlineData(Ctrl, Key.K, AppCommand.Palette)]
    [InlineData(Alt, Key.Up, AppCommand.PreviousView)]
    [InlineData(Alt, Key.Down, AppCommand.NextView)]
    [InlineData(Ctrl, Key.Comma, AppCommand.Settings)]
    public void The_window_answers_to_the_ones_that_work_anywhere(HotkeyModifiers modifiers, Key key, AppCommand expected)
        => Assert.Equal(expected, In(ShortcutScope.Window, modifiers, key));

    [Theory]
    [InlineData(Key.Plus, AppCommand.ZoomIn)]
    [InlineData(Key.NumPadPlus, AppCommand.ZoomIn)]
    [InlineData(Key.Minus, AppCommand.ZoomOut)]
    [InlineData(Key.NumPadMinus, AppCommand.ZoomOut)]
    [InlineData(Key.Digit0, AppCommand.ZoomReset)]
    [InlineData(Key.NumPad0, AppCommand.ZoomReset)]
    public void Zooming_answers_on_the_number_pad_as_well_as_the_row_above_the_letters(Key key, AppCommand expected)
        => Assert.Equal(expected, In(ShortcutScope.Window, Ctrl, key));

    [Fact]
    public void The_panel_is_shown_by_F4_and_its_tabs_picked_by_their_own_keys()
    {
        // F4 is the only toggle of the three; the other two name a tab.
        Assert.Equal(AppCommand.ToggleDescription, In(ShortcutScope.Window, None, Key.F4));
        Assert.Equal(AppCommand.ViewDescription, In(ShortcutScope.Window, None, Key.F6));
        Assert.Equal(AppCommand.ViewComments, In(ShortcutScope.Window, None, Key.F7));

        // F4 in the outline is nothing, so the window's own answer is the one that stands there.
        Assert.Equal(AppCommand.None, In(ShortcutScope.Outline, None, Key.F4));

        // And the keys the tabs had before are theirs no longer. Still claimed window-wide, Ctrl+M
        // would be answered before the outline ever saw it, and the move it now means wouldn't run.
        Assert.Equal(AppCommand.None, In(ShortcutScope.Window, Ctrl, Key.E));
        Assert.Equal(AppCommand.None, In(ShortcutScope.Window, Ctrl, Key.M));
    }

    [Fact]
    public void An_arrow_means_the_task_with_Ctrl_and_the_view_with_Alt()
    {
        // Moving a task is done often and from the outline; changing view is rarer and has to work
        // from anywhere. The two were the other way round, which put the commoner move on the key
        // that had to reach across the whole window.
        Assert.Equal(AppCommand.MoveUp, In(ShortcutScope.Outline, Ctrl, Key.Up));
        Assert.Equal(AppCommand.PreviousView, In(ShortcutScope.Window, Alt, Key.Up));

        // And neither has kept the other's key anywhere.
        Assert.Equal(AppCommand.None, In(ShortcutScope.Outline, Alt, Key.Up));
        Assert.Equal(AppCommand.None, In(ShortcutScope.Window, Ctrl, Key.Up));
    }

    [Fact]
    public void Moving_a_project_asks_for_more_than_moving_a_task()
    {
        // Shift as well as Ctrl, and only from the sidebar. Reordering the sidebar is rare and
        // deliberate, and the harder reach is the point — a project shifted by a stray finger is a
        // change nobody sees happen and nobody thinks to look for.
        Assert.Equal(AppCommand.MoveSelectionUp, In(ShortcutScope.Sidebar, Ctrl | Shift, Key.Up));
        Assert.Equal(AppCommand.MoveSelectionDown, In(ShortcutScope.Sidebar, Ctrl | Shift, Key.Down));

        // Not from the outline, where the same fingers a shift away move the task instead, and not
        // window-wide, where it would fire over whatever had the focus.
        Assert.Equal(AppCommand.None, In(ShortcutScope.Outline, Ctrl | Shift, Key.Up));
        Assert.Equal(AppCommand.None, In(ShortcutScope.Window, Ctrl | Shift, Key.Up));

        // And the sidebar doesn't answer to the task's own binding.
        Assert.Equal(AppCommand.None, In(ShortcutScope.Sidebar, Ctrl, Key.Up));
    }

    [Fact]
    public void Tab_is_left_to_move_the_focus()
    {
        // It indented here once, which is what a to-do list does and what every other window in
        // Windows does not. Ctrl and an arrow says the same thing without taking the one key a
        // keyboard user needs to get out of a list.
        Assert.Equal(AppCommand.None, In(ShortcutScope.Outline, None, Key.Tab));
        Assert.Equal(AppCommand.None, In(ShortcutScope.Outline, Shift, Key.Tab));

        Assert.Equal(AppCommand.Indent, In(ShortcutScope.Outline, Ctrl, Key.Right));
        Assert.Equal(AppCommand.Outdent, In(ShortcutScope.Outline, Ctrl, Key.Left));
    }

    [Fact]
    public void The_same_key_means_a_different_thing_in_each_list()
    {
        // F2 and Delete belong to both lists, and which one is meant is decided by where the user
        // is — not by one of the two winning outright.
        Assert.Equal(AppCommand.Rename, In(ShortcutScope.Outline, None, Key.F2));
        Assert.Equal(AppCommand.RenameSelection, In(ShortcutScope.Sidebar, None, Key.F2));

        Assert.Equal(AppCommand.Delete, In(ShortcutScope.Outline, None, Key.Delete));
        Assert.Equal(AppCommand.DeleteSelection, In(ShortcutScope.Sidebar, None, Key.Delete));

        // And neither is claimed window-wide, where it would fire whatever had the focus.
        Assert.Equal(AppCommand.None, In(ShortcutScope.Window, None, Key.F2));
        Assert.Equal(AppCommand.None, In(ShortcutScope.Window, None, Key.Delete));
    }

    [Fact]
    public void Moving_a_task_elsewhere_answers_only_in_the_outline()
    {
        // Where there's a task under the cursor to move. Window-wide it would fire over the
        // description editor and the comment box as well, and open the picker on a task nobody was
        // looking at.
        Assert.Equal(AppCommand.MoveTo, In(ShortcutScope.Outline, Ctrl, Key.M));
        Assert.Equal(AppCommand.None, In(ShortcutScope.Sidebar, Ctrl, Key.M));
    }

    [Fact]
    public void Showing_a_task_in_todoist_answers_only_in_the_outline()
    {
        // The task under the cursor is the one that goes to the browser, so there has to be one.
        // Window-wide it would also take Ctrl+O from the description editor and the comment box.
        Assert.Equal(AppCommand.ShowInTodoist, In(ShortcutScope.Outline, Ctrl, Key.O));
        Assert.Equal(AppCommand.None, In(ShortcutScope.Sidebar, Ctrl, Key.O));
        Assert.Equal(AppCommand.None, In(ShortcutScope.Window, Ctrl, Key.O));
        Assert.Equal("Ctrl+O", Shortcuts.ShortcutFor(AppCommand.ShowInTodoist));
    }

    [Fact]
    public void Undo_is_not_claimed_window_wide()
    {
        // Taken window-wide it would reach the capture and search boxes, where Ctrl+Z has to go on
        // undoing the word just typed rather than the last write to the account.
        Assert.Equal(AppCommand.None, In(ShortcutScope.Window, Ctrl, Key.Z));
        Assert.Equal(AppCommand.Undo, In(ShortcutScope.Outline, Ctrl, Key.Z));
    }

    [Theory]
    [InlineData(ShortcutScope.Outline, None, Key.A)]
    [InlineData(ShortcutScope.Window, None, Key.Escape)]

    // The palette belongs to the window, and due dates to the outline; neither answers from where
    // the other lives.
    [InlineData(ShortcutScope.Outline, Ctrl, Key.K)]
    [InlineData(ShortcutScope.Sidebar, Ctrl, Key.D)]
    public void A_keystroke_that_means_nothing_there_asks_for_nothing(ShortcutScope scope, HotkeyModifiers modifiers, Key key)
        => Assert.Equal(AppCommand.None, In(scope, modifiers, key));

    [Theory]
    [InlineData(Ctrl, Key.Digit1, "Ctrl+1")]
    [InlineData(Ctrl, Key.Digit4, "Ctrl+4")]
    [InlineData(Ctrl, Key.Right, "Ctrl+→")]
    [InlineData(Ctrl | Shift, Key.Up, "Ctrl+Shift+↑")]
    [InlineData(Ctrl, Key.Left, "Ctrl+←")]
    [InlineData(Alt, Key.Up, "Alt+↑")]
    [InlineData(Alt, Key.Down, "Alt+↓")]
    [InlineData(None, Key.Delete, "Del")]
    [InlineData(None, Key.Space, "Space")]
    [InlineData(None, Key.F2, "F2")]
    [InlineData(None, Key.F6, "F6")]
    [InlineData(Ctrl, Key.M, "Ctrl+M")]
    [InlineData(Ctrl, Key.Comma, "Ctrl+,")]
    [InlineData(Ctrl | Shift, Key.N, "Ctrl+Shift+N")]
    [InlineData(Ctrl, Key.Enter, "Ctrl+Enter")]
    [InlineData(Ctrl | Alt | Shift, Key.N, "Ctrl+Shift+Alt+N")]
    [InlineData(Ctrl, Key.Plus, "Ctrl++")]
    [InlineData(Ctrl, Key.NumPadMinus, "Ctrl+-")]
    [InlineData(Ctrl, Key.NumPad0, "Ctrl+0")]
    public void A_shortcut_is_written_the_way_a_menu_writes_it(HotkeyModifiers modifiers, Key key, string expected)
        => Assert.Equal(expected, new Keystroke(modifiers, key).ToString());

    [Fact]
    public void The_shortcut_printed_for_completing_is_the_bare_key_not_the_second_binding()
    {
        // Space and Ctrl+Enter both tick a task off. A menu has room for one, and it should be the
        // one that is easier to reach.
        Assert.Equal("Space", Shortcuts.ShortcutFor(AppCommand.ToggleComplete));
    }

    [Fact]
    public void New_section_prints_no_shortcut_of_its_own()
    {
        // Ctrl+N reaches it, but only from the sidebar and only over a project — printed beside
        // New section it would read as a second, unconditional binding for the same keystroke that
        // New task already claims.
        Assert.Equal(string.Empty, Shortcuts.ShortcutFor(AppCommand.NewSection));
        Assert.Equal("Ctrl+N", Shortcuts.ShortcutFor(AppCommand.NewTask));
    }
}
