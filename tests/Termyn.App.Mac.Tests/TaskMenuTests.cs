using Termyn.Presentation;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// The Task menu and the right-click menu, as they come out of the portable list.
/// </summary>
/// <remarks>
/// What's expected is worked out from <see cref="Menus.TaskContext"/> and
/// <see cref="TaskCommands.Offered"/> rather than written out, so these hold while commands join the
/// list. A menu is read as a list of its items' commands, with a rule as "-" and a submenu's
/// contents in brackets after its name.
/// </remarks>
public class TaskMenuTests
{
    [AppKitFact]
    public void The_items_and_rules_are_the_portable_lists_less_what_isnt_offered()
        => Assert.Equal(Expected(Menus.TaskContext), Shape(TaskMenu.Build("Task")));

    [AppKitFact]
    public void No_rule_is_stranded_at_an_end_or_doubled()
    {
        var menu = TaskMenu.Build("Task");

        Assert.All(MenuAndSubmenus(menu), m =>
        {
            var rules = m.Items.Select(i => i.IsSeparatorItem).ToList();

            Assert.NotEmpty(rules);
            Assert.False(rules[0], $"{m.Title} opens with a rule");
            Assert.False(rules[^1], $"{m.Title} ends with a rule");
            Assert.DoesNotContain(rules.Zip(rules.Skip(1)), pair => pair is { First: true, Second: true });
        });
    }

    [AppKitFact]
    public void Each_item_sends_the_one_action_with_its_command_in_the_tag()
    {
        var items = MenuAndSubmenus(TaskMenu.Build("Task"))
            .SelectMany(m => m.Items)
            .Where(i => !i.IsSeparatorItem && !i.HasSubmenu)
            .ToList();

        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.Equal(TaskMenu.Action, item.Action!.Name);
            Assert.Contains(TaskMenu.CommandOf(item), TaskCommands.Offered);
        });
    }

    [AppKitFact]
    public void An_item_carries_the_key_its_command_answers_to()
    {
        var delete = MenuAndSubmenus(TaskMenu.Build("Task"))
            .SelectMany(m => m.Items)
            .Single(i => !i.IsSeparatorItem && !i.HasSubmenu && TaskMenu.CommandOf(i) == AppCommand.Delete);
        var key = MacShortcuts.For(AppCommand.Delete)!.Value;

        Assert.Equal(key.Key, delete.KeyEquivalent);
        Assert.Equal(key.Modifiers, delete.KeyEquivalentModifierMask);
    }

    [AppKitFact]
    public void A_real_press_of_its_key_runs_an_item()
    {
        // ⌘⌫ above all: the item holds \b, and the key itself reports \u007F.
        Assert.Equal(1, Runs(AppCommand.Delete, MacShortcutsTests.RealPress(MacShortcutsTests.DeleteKey, CGEventFlags.Command)));
        Assert.Equal(1, Runs(AppCommand.ToggleComplete, MacShortcutsTests.RealPress(MacShortcutsTests.ReturnKey, CGEventFlags.Command)));
        Assert.Equal(1, Runs(AppCommand.MoveUp, MacShortcutsTests.RealPress(MacShortcutsTests.UpKey, CGEventFlags.Command | CGEventFlags.Alternate)));
    }

    [AppKitFact]
    public void The_submenus_are_named_in_the_Macs_words()
    {
        // The portable list's own headings carry the Windows mnemonic.
        var submenus = TaskMenu.Build("Task").Items.Where(i => i.HasSubmenu).Select(i => i.Title);

        Assert.Equal(["Schedule", "Priority"], submenus);
    }

    /// <summary>How many times a key press reached a command's item, with the window controller's part played.</summary>
    private static int Runs(AppCommand command, NSEvent press)
    {
        var menu = TaskMenu.Build("Task");
        menu.AutoEnablesItems = false;

        using var answer = new Answer();
        var item = MenuAndSubmenus(menu)
            .SelectMany(m => m.Items)
            .Single(i => !i.IsSeparatorItem && !i.HasSubmenu && TaskMenu.CommandOf(i) == command);
        item.Target = answer;

        menu.PerformKeyEquivalent(press);
        return answer.Count;
    }

    /// <summary>Stands where the window controller does, counting what reaches it.</summary>
    private sealed class Answer : NSObject
    {
        public int Count { get; private set; }

        [Export(TaskMenu.Action)]
        public void Run(NSObject sender) => Count++;
    }

    /// <summary>What a menu built from these entries should hold: a rule only between two things that are both there.</summary>
    private static List<string> Expected(IReadOnlyList<MenuEntry> entries)
    {
        var shape = new List<string>();
        var rule = false;

        foreach (var entry in entries)
        {
            rule |= entry.SeparatorBefore;

            var inside = entry.Children is { } children ? Expected(children) : null;
            if (inside is { Count: 0 } || (inside is null && !TaskCommands.Offered.Contains(entry.Command)))
                continue;

            if (rule && shape.Count > 0)
                shape.Add("-");
            rule = false;

            shape.Add(inside is null ? entry.Command.ToString() : $"[{string.Join(" ", inside)}]");
        }

        return shape;
    }

    /// <summary>What a built menu holds, in the same terms.</summary>
    private static List<string> Shape(NSMenu menu) => menu.Items
        .Select(item => item.IsSeparatorItem ? "-"
            : item.HasSubmenu ? $"[{string.Join(" ", Shape(item.Submenu!))}]"
            : TaskMenu.CommandOf(item).ToString())
        .ToList();

    private static IEnumerable<NSMenu> MenuAndSubmenus(NSMenu menu)
        => menu.Items.Where(i => i.HasSubmenu).SelectMany(i => MenuAndSubmenus(i.Submenu!)).Prepend(menu);
}
