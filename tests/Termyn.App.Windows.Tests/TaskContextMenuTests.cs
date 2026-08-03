using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The menu as it is actually built — the strip a right-click puts on screen, rather than the model
/// it is built from.
/// </summary>
public class TaskContextMenuTests
{
    private static TaskRow Row(Priority priority = Priority.P4, bool completed = false)
        => new("i1", "Write it up", priority, "Work", string.Empty, [], Completed: completed);

    /// <summary>A built menu and the commands its items have raised so far.</summary>
    private sealed record Built(ContextMenuStrip Menu, List<TaskCommand> Ran) : IDisposable
    {
        public void Dispose() => Menu.Dispose();
    }

    /// <summary>Builds the menu for a row, ready to be read or clicked.</summary>
    private static Built Build(TaskRow row)
    {
        var ran = new List<TaskCommand>();
        var menu = new ContextMenuStrip();
        MainForm.FillTaskMenu(menu.Items, TaskMenu.For(row), ran.Add);
        return new Built(menu, ran);
    }

    private static IEnumerable<ToolStripMenuItem> Every(ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            yield return item;
            foreach (var child in Every(item.DropDownItems))
                yield return child;
        }
    }

    [Fact]
    public void Every_action_shows_its_keyboard_shortcut()
    {
        using var built = Build(Row());

        // The headings are the only things without one, because they do nothing to have a shortcut
        // for. Anything else blank would be a row of the menu teaching nothing.
        var blank = Every(built.Menu.Items)
            .Where(i => i.DropDownItems.Count == 0)
            .Where(i => string.IsNullOrEmpty(i.ShortcutKeyDisplayString))
            .Select(i => i.Text)
            .ToList();

        Assert.Empty(blank);
    }

    [Fact]
    public void The_shortcuts_are_the_ones_the_outline_answers_to()
    {
        using var built = Build(Row());

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
    public void No_shortcut_is_bound_to_the_menu_itself()
    {
        using var built = Build(Row());

        // Bound here as well as on the outline, the two would both answer and only one of them
        // would be ours to reason about. The menu prints the keystroke; it does not claim it.
        Assert.All(Every(built.Menu.Items), i => Assert.Equal(Keys.None, i.ShortcutKeys));
    }

    [Fact]
    public void Clicking_an_entry_runs_that_entry_and_not_the_last_one_built()
    {
        using var built = Build(Row());

        // Every item, not one: with a single item "runs its own command" and "runs the command the
        // loop finished on" are the same thing, and the bug this guards against would walk past.
        foreach (var item in Every(built.Menu.Items).Where(i => i.DropDownItems.Count == 0))
            item.PerformClick();

        Assert.Equal(TaskMenu.Commands(TaskMenu.For(Row())).ToArray(), built.Ran.ToArray());
    }

    [Fact]
    public void Clicking_a_heading_runs_nothing()
    {
        using var built = Build(Row());

        foreach (var heading in Every(built.Menu.Items).Where(i => i.DropDownItems.Count > 0))
            heading.PerformClick();

        Assert.Empty(built.Ran);
    }

    [Fact]
    public void The_priority_the_task_is_on_is_ticked_in_the_menu()
    {
        using var built = Build(Row(Priority.P2));

        var ticked = Every(built.Menu.Items).Where(i => i.Checked).ToList();

        Assert.Equal("Priority 2 — high", Assert.Single(ticked).Text);
    }

    [Fact]
    public void A_completed_task_is_offered_reopening()
    {
        using var built = Build(Row(completed: true));

        var labels = Every(built.Menu.Items).Select(i => i.Text).ToList();

        Assert.Contains("Reopen", labels);
        Assert.DoesNotContain("Complete", labels);
    }

    [Fact]
    public void The_groups_are_ruled_off_from_one_another()
    {
        using var built = Build(Row());

        // Three rules: what the task is, when and what it carries, where it sits, and then delete
        // on its own. None of them at the top or the bottom, where a rule has nothing to divide.
        Assert.Equal(3, built.Menu.Items.OfType<ToolStripSeparator>().Count());
        Assert.IsNotType<ToolStripSeparator>(built.Menu.Items[0]);
        Assert.IsNotType<ToolStripSeparator>(built.Menu.Items[^1]);
    }

    [Fact]
    public void The_priorities_are_a_submenu_rather_than_four_more_rows()
    {
        using var built = Build(Row());

        var priorities = built.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.DropDownItems.Count > 0);

        Assert.Equal("Priority", priorities.Text);
        Assert.Equal(4, priorities.DropDownItems.Count);
    }
}
