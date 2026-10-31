using ObjCRuntime;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The Task menu and the right-click menu on a task, built from the same portable list the Windows
/// menus are, so the two can't come to disagree about what sits where.
/// </summary>
/// <remarks>
/// Every item sends <see cref="Action"/> down the responder chain with its command in the tag, and
/// the window controller answers it — and, before the menu shows, validates it, which is where each
/// item's name, tick and whether it's enabled are filled in. Commands this window doesn't offer yet
/// are left out, along with any rule that leaves stranded.
/// </remarks>
internal static class TaskMenu
{
    /// <summary>The action every item sends. The window controller answers it.</summary>
    public const string Action = "runTaskCommand:";

    public static NSMenu Build(string title)
    {
        var menu = new NSMenu(title);
        Fill(menu, Menus.TaskContext);
        return menu;
    }

    /// <summary>The command an item runs, from its tag.</summary>
    public static AppCommand CommandOf(NSMenuItem item) => (AppCommand)(int)item.Tag;

    private static void Fill(NSMenu menu, IReadOnlyList<MenuEntry> entries)
    {
        var rule = false;

        foreach (var entry in entries)
        {
            rule |= entry.SeparatorBefore;

            NSMenuItem item;
            if (entry.Children is { } children)
            {
                if (!children.Any(c => TaskCommands.Offered.Contains(c.Command)))
                    continue;

                var sub = new NSMenu(Heading(children));
                Fill(sub, children);
                item = new NSMenuItem(sub.Title) { Submenu = sub };
            }
            else
            {
                if (!TaskCommands.Offered.Contains(entry.Command))
                    continue;

                // The title is filled in when the menu's validated; this one is only until then.
                item = new NSMenuItem(entry.Command.ToString(), new Selector(Action), string.Empty) { Tag = (int)entry.Command };
                if (MacShortcuts.For(entry.Command) is { } key)
                {
                    item.KeyEquivalent = key.Key;
                    item.KeyEquivalentModifierMask = key.Modifiers;
                }
            }

            // A rule only between two things that are both there.
            if (rule && menu.Count > 0)
                menu.AddItem(NSMenuItem.SeparatorItem);
            rule = false;

            menu.AddItem(item);
        }
    }

    /// <summary>
    /// A submenu's name, in the Mac's words. The portable list's own headings carry the Windows
    /// mnemonic, and are named by what's in them anyway.
    /// </summary>
    private static string Heading(IReadOnlyList<MenuEntry> children)
        => children.Any(c => Commands.PriorityOf(c.Command) is not null) ? Strings.MenuPriority : Strings.MenuSchedule;
}
