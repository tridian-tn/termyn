using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The menus as they are actually built — the strips that go on screen, rather than the lists they
/// are built from.
/// </summary>
public class MenuTests
{
    private static TaskRow Row(Priority priority = Priority.P4, bool completed = false)
        => new("i1", "Write it up", priority, "Work", string.Empty, [], Completed: completed);

    private static CommandContext OnTask(TaskRow? row = null, TaskAbilities? can = null)
        => new(row ?? Row(), can ?? new TaskAbilities(true, true, true, true));

    private static SidebarNode Node(SidebarKind kind, bool favourite = false)
        => new(kind, "id", "Work", 1, "key", IsFavorite: favourite);

    /// <summary>A built menu and the commands its items have raised so far.</summary>
    private sealed record Built(ContextMenuStrip Menu, List<AppCommand> Ran) : IDisposable
    {
        public void Dispose() => Menu.Dispose();
    }

    private static Built Build(IReadOnlyList<MenuEntry> entries, CommandContext context)
    {
        var ran = new List<AppCommand>();
        var menu = new ContextMenuStrip();
        MainForm.FillMenu(menu.Items, entries, context, MainForm.ShortcutFor, ran.Add);
        return new Built(menu, ran);
    }

    private static Built BuildTaskMenu(CommandContext context) => Build(Menus.TaskContext, context);

    private static IEnumerable<ToolStripMenuItem> Every(ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            yield return item;
            foreach (var child in Every(item.DropDownItems))
                yield return child;
        }
    }

    private static ToolStripMenuItem Find(Built built, string label)
        => Every(built.Menu.Items).Single(i => i.Text == label);

    // ---- What is offered -----------------------------------------------------------------------

    [Fact]
    public void The_Task_menu_and_the_right_click_menu_hold_the_same_actions()
    {
        // Requirement, not coincidence: they are the same list, so an action can't be added to one
        // and forgotten in the other.
        var bar = Menus.Bar.Single(e => e.Heading == "&Task");

        Assert.Equal(
            Menus.Commands(Menus.TaskContext).ToArray(),
            Menus.Commands(bar.Children ?? []).ToArray());
    }

    [Fact]
    public void Every_action_the_outline_takes_on_a_task_is_in_the_task_menu()
    {
        Assert.Equal(
            [
                AppCommand.ToggleComplete,
                AppCommand.Rename,
                AppCommand.Due,
                AppCommand.Priority1,
                AppCommand.Priority2,
                AppCommand.Priority3,
                AppCommand.Priority4,
                AppCommand.Labels,
                AppCommand.Reminders,
                AppCommand.Indent,
                AppCommand.Outdent,
                AppCommand.MoveUp,
                AppCommand.MoveDown,
                AppCommand.Delete,
            ],
            Menus.Commands(Menus.TaskContext).ToArray());
    }

    [Fact]
    public void The_menu_bar_is_laid_out_in_the_expected_order()
        => Assert.Equal(
            ["&File", "&Edit", "&View", "&Task", "&Organise", "&Help"],
            Menus.Bar.Select(e => e.Heading ?? string.Empty).ToArray());

    [Fact]
    public void Every_command_the_app_can_run_is_somewhere_in_the_menu_bar()
    {
        // The bar is meant to be the whole of what the app does, so that nothing is reachable only
        // by a keystroke nobody has been told about. Quick-add is the exception in reverse: it has
        // no place in a list of the app's own commands beyond File, where it already is.
        var inTheBar = Menus.Commands(Menus.Bar).ToHashSet();

        var missing = Enum.GetValues<AppCommand>()
            .Where(c => c != AppCommand.None)
            .Where(c => !inTheBar.Contains(c))
            .ToList();

        Assert.Empty(missing);
    }

    // ---- Naming and ticking --------------------------------------------------------------------

    [Fact]
    public void A_task_still_to_do_is_offered_completion()
    {
        using var built = BuildTaskMenu(OnTask());

        Assert.Contains(Every(built.Menu.Items), i => i.Text == "Complete");
    }

    [Fact]
    public void A_task_already_done_is_offered_reopening_instead()
    {
        using var built = BuildTaskMenu(OnTask(Row(completed: true)));

        var labels = Every(built.Menu.Items).Select(i => i.Text).ToList();

        Assert.Contains("Reopen", labels);
        Assert.DoesNotContain("Complete", labels);
    }

    [Fact]
    public void The_priority_the_task_is_on_is_ticked()
    {
        using var built = BuildTaskMenu(OnTask(Row(Priority.P2)));

        Assert.Equal("Priority 2 — high", Assert.Single(Every(built.Menu.Items), i => i.Checked).Text);
    }

    [Fact]
    public void The_sidebar_actions_are_named_for_what_is_selected()
    {
        using var project = Build(Organise, new CommandContext(Selection: Node(SidebarKind.Project)));
        using var label = Build(Organise, new CommandContext(Selection: Node(SidebarKind.Label)));

        // "Rename" alone over a sidebar holding projects, sections and labels doesn't say which of
        // the three is about to change.
        Assert.Contains(Every(project.Menu.Items), i => i.Text == "Rename project");
        Assert.Contains(Every(label.Menu.Items), i => i.Text == "Delete label");
    }

    [Fact]
    public void The_favourite_entry_says_which_way_it_would_go()
    {
        using var plain = Build(Organise, new CommandContext(Selection: Node(SidebarKind.Project)));
        using var starred = Build(Organise, new CommandContext(Selection: Node(SidebarKind.Project, favourite: true)));

        Assert.Contains(Every(plain.Menu.Items), i => i.Text == "Add to favourites");
        Assert.Contains(Every(starred.Menu.Items), i => i.Text == "Remove from favourites");
    }

    // ---- What is greyed ------------------------------------------------------------------------

    [Fact]
    public void With_no_task_selected_nothing_in_the_task_menu_can_be_run()
    {
        using var built = BuildTaskMenu(CommandContext.Empty);

        var runnable = Every(built.Menu.Items).Where(i => i.DropDownItems.Count == 0);

        Assert.All(runnable, i => Assert.False(i.Enabled, $"{i.Text} was offered with no task selected"));
    }

    [Fact]
    public void A_submenu_with_nothing_runnable_in_it_is_greyed_too()
    {
        using var built = BuildTaskMenu(CommandContext.Empty);

        // Left enabled it would open onto four greyed priorities, which is a longer way of saying
        // the same thing.
        Assert.False(Find(built, "&Priority").Enabled);
    }

    [Fact]
    public void A_task_at_the_bottom_of_its_list_is_not_offered_a_move_down()
    {
        using var built = BuildTaskMenu(OnTask(can: new TaskAbilities(CanMoveUp: true, CanMoveDown: false)));

        Assert.True(Find(built, "Move up").Enabled);
        Assert.False(Find(built, "Move down").Enabled);
    }

    [Fact]
    public void A_task_at_the_top_level_is_not_offered_an_outdent()
    {
        using var built = BuildTaskMenu(OnTask(can: new TaskAbilities(CanIndent: true, CanOutdent: false)));

        Assert.True(Find(built, "Indent").Enabled);
        Assert.False(Find(built, "Outdent").Enabled);
    }

    [Fact]
    public void What_can_be_done_to_a_task_does_not_grey_what_is_always_possible()
    {
        // A task that can't move anywhere can still be renamed, dated and deleted.
        using var built = BuildTaskMenu(OnTask(can: new TaskAbilities()));

        Assert.True(Find(built, "Complete").Enabled);
        Assert.True(Find(built, "Rename").Enabled);
        Assert.True(Find(built, "Due date…").Enabled);
        Assert.True(Find(built, "Delete").Enabled);
        Assert.False(Find(built, "Move up").Enabled);
    }

    [Fact]
    public void Undo_is_greyed_when_there_is_nothing_to_take_back()
    {
        using var nothing = Build(Edit, CommandContext.Empty);
        using var something = Build(Edit, new CommandContext(CanUndo: true));

        Assert.False(Find(nothing, "Undo").Enabled);
        Assert.True(Find(something, "Undo").Enabled);
    }

    [Fact]
    public void A_new_section_needs_a_project_to_put_it_in()
    {
        using var nowhere = Build(File, CommandContext.Empty);
        using var project = Build(File, new CommandContext(Selection: Node(SidebarKind.Project)));

        Assert.False(Find(nowhere, "New section").Enabled);
        Assert.True(Find(project, "New section").Enabled);
    }

    [Fact]
    public void A_section_has_no_star_to_take_off()
    {
        using var built = Build(Organise, new CommandContext(Selection: Node(SidebarKind.Section)));

        // Renameable and deletable, but Todoist gives it no favourite of its own.
        Assert.True(Find(built, "Rename section").Enabled);
        Assert.False(Find(built, "Add to favourites").Enabled);
    }

    [Fact]
    public void Nothing_in_the_sidebar_menu_is_offered_over_a_smart_view()
    {
        using var built = Build(Organise, new CommandContext(Selection: Node(SidebarKind.SmartView)));

        Assert.All(Every(built.Menu.Items), i => Assert.False(i.Enabled, $"{i.Text} was offered over Today"));
    }

    [Fact]
    public void The_way_back_to_the_default_order_is_offered_only_once_there_is_one()
    {
        using var unsorted = Build(View, CommandContext.Empty);
        using var sorted = Build(View, new CommandContext(Sort: new TaskSort(TaskColumn.Due)));

        // Greyed until a column has been clicked, which is also how the entry says which of the
        // two orders you are currently looking at.
        Assert.False(Find(unsorted, "Default order").Enabled);
        Assert.True(Find(sorted, "Default order").Enabled);
    }

    [Fact]
    public void The_description_entry_keeps_one_name_and_is_ticked_while_the_panel_is_open()
    {
        // It used to read "Show description" and then "Hide description", which is a different
        // entry each time you look. The tick was already saying which state you were in.
        using var closed = Build(View, CommandContext.Empty);
        using var open = Build(View, new CommandContext(ShowingDescription: true));

        Assert.False(Find(closed, "Description").Checked);
        Assert.True(Find(open, "Description").Checked);
    }

    [Fact]
    public void Editing_the_description_is_offered_only_while_the_panel_it_happens_in_is_open()
    {
        using var closed = Build(View, CommandContext.Empty);
        using var reading = Build(View, new CommandContext(ShowingDescription: true));
        using var writing = Build(View, new CommandContext(ShowingDescription: true, WritingDescription: true));

        Assert.False(Find(closed, "Edit description").Enabled);
        Assert.True(Find(reading, "Edit description").Enabled);
        Assert.False(Find(reading, "Edit description").Checked);

        // Ticked while the markdown is on show, since that is the state you leave rather than the
        // one the panel rests in. Ticked and not renamed — the tick is the only thing that moves.
        Assert.True(Find(writing, "Edit description").Checked);
    }

    [Fact]
    public void The_comments_entry_is_ticked_while_the_pane_is_showing_them()
    {
        using var description = Build(View, new CommandContext(ShowingDescription: true));
        using var comments = Build(View, new CommandContext(ShowingDescription: true, ShowingComments: true));

        Assert.False(Find(description, "Comments").Checked);
        Assert.True(Find(comments, "Comments").Checked);
    }

    [Fact]
    public void Editing_the_description_is_not_offered_while_the_pane_is_on_the_comments()
    {
        // The same pane showing a third thing. Offering "Edit description" there would put the
        // markdown behind the comments, where the typing would go somewhere nobody can see.
        using var comments = Build(View, new CommandContext(ShowingDescription: true, ShowingComments: true));

        Assert.False(Find(comments, "Edit description").Enabled);
    }

    [Fact]
    public void A_projects_own_comments_are_offered_only_over_a_project()
    {
        using var project = Build(Organise, new CommandContext(Selection: Node(SidebarKind.Project)));
        using var label = Build(Organise, new CommandContext(Selection: Node(SidebarKind.Label)));
        using var nothing = Build(Organise, CommandContext.Empty);

        Assert.True(Find(project, "Comments on project").Enabled);
        Assert.False(Find(label, "Comments on project").Enabled);
        Assert.False(Find(nothing, "Comments on project").Enabled);
    }

    [Fact]
    public void Completed_tasks_keeps_one_name_and_is_ticked_while_they_are_showing()
    {
        // A menu has a tick, so the entry has no reason to rename itself and every reason not to.
        using var hidden = Build(View, CommandContext.Empty);
        using var shown = Build(View, new CommandContext(ShowingCompleted: true));

        Assert.False(Find(hidden, "Completed tasks").Checked);
        Assert.True(Find(shown, "Completed tasks").Checked);
    }

    // ---- Shortcuts and wiring ------------------------------------------------------------------

    [Fact]
    public void The_shortcuts_shown_are_the_ones_the_outline_answers_to()
    {
        using var built = BuildTaskMenu(OnTask());

        var shown = Every(built.Menu.Items)
            .Where(i => i.DropDownItems.Count == 0)
            .ToDictionary(i => i.Text!, i => i.ShortcutKeyDisplayString);

        Assert.Equal("Space", shown["Complete"]);
        Assert.Equal("F2", shown["Rename"]);
        Assert.Equal("Ctrl+D", shown["Due date…"]);
        Assert.Equal("Ctrl+L", shown["Labels…"]);
        Assert.Equal("Ctrl+R", shown["Reminders…"]);
        Assert.Equal("Tab", shown["Indent"]);
        Assert.Equal("Shift+Tab", shown["Outdent"]);
        Assert.Equal("Alt+↑", shown["Move up"]);
        Assert.Equal("Alt+↓", shown["Move down"]);
        Assert.Equal("Del", shown["Delete"]);
    }

    [Fact]
    public void No_shortcut_is_bound_to_a_menu_item_itself()
    {
        using var built = BuildTaskMenu(OnTask());

        // Bound here as well as on the control, the two would both answer and only one of them
        // would be ours to reason about. A menu prints the keystroke; it does not claim it.
        Assert.All(Every(built.Menu.Items), i => Assert.Equal(Keys.None, i.ShortcutKeys));
    }

    [Fact]
    public void Clicking_an_entry_runs_that_entry_and_not_the_last_one_built()
    {
        using var built = BuildTaskMenu(OnTask());

        // Every item, not one: with a single item "runs its own command" and "runs the command the
        // loop finished on" are the same thing, and the bug this guards against would walk past.
        foreach (var item in Every(built.Menu.Items).Where(i => i.DropDownItems.Count == 0))
            item.PerformClick();

        Assert.Equal(Menus.Commands(Menus.TaskContext).ToArray(), built.Ran.ToArray());
    }

    [Fact]
    public void Clicking_a_heading_runs_nothing()
    {
        using var built = BuildTaskMenu(OnTask());

        foreach (var heading in Every(built.Menu.Items).Where(i => i.DropDownItems.Count > 0))
            heading.PerformClick();

        Assert.Empty(built.Ran);
    }

    // ---- Layout --------------------------------------------------------------------------------

    [Fact]
    public void The_groups_are_ruled_off_from_one_another()
    {
        using var built = BuildTaskMenu(OnTask());

        // Three rules: what the task is, what it carries, where it sits, and then delete on its
        // own. None at the top or the bottom, where a rule has nothing to divide.
        Assert.Equal(3, built.Menu.Items.OfType<ToolStripSeparator>().Count());
        Assert.IsNotType<ToolStripSeparator>(built.Menu.Items[0]);
        Assert.IsNotType<ToolStripSeparator>(built.Menu.Items[^1]);
    }

    [Fact]
    public void The_priorities_are_a_submenu_rather_than_four_more_rows()
    {
        using var built = BuildTaskMenu(OnTask());

        var priorities = built.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.DropDownItems.Count > 0);

        Assert.Equal("&Priority", priorities.Text);
        Assert.Equal(4, priorities.DropDownItems.Count);
    }

    [Fact]
    public void No_menu_opens_on_a_separator()
    {
        // A leading rule draws itself against the top edge of the menu.
        foreach (var group in Menus.Bar)
        {
            using var built = Build(group.Children ?? [], CommandContext.Empty);
            Assert.IsNotType<ToolStripSeparator>(built.Menu.Items[0]);
        }
    }

    [Fact]
    public void Every_entry_in_every_menu_is_labelled()
    {
        foreach (var group in Menus.Bar)
        {
            using var built = Build(group.Children ?? [], OnTask());
            Assert.All(Every(built.Menu.Items), i => Assert.False(string.IsNullOrWhiteSpace(i.Text)));
        }
    }

    private static IReadOnlyList<MenuEntry> Group(string heading)
        => Menus.Bar.Single(e => e.Heading == heading).Children ?? [];

    private static IReadOnlyList<MenuEntry> File => Group("&File");

    private static IReadOnlyList<MenuEntry> Edit => Group("&Edit");

    private static IReadOnlyList<MenuEntry> View => Group("&View");

    private static IReadOnlyList<MenuEntry> Organise => Group("&Organise");
}
