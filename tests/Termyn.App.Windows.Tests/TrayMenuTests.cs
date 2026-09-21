using Termyn.Core.Platform;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The tray icon's menu: what it offers without the window, and where the views sit in it.
/// </summary>
/// <remarks>
/// Built away from the window so the whole menu can be read here — its order, its rules, and which
/// view each entry opens. What a desktop does with the list is the notifier's business.
/// </remarks>
public class TrayMenuTests
{
    private static SidebarNode View(string label, string key)
        => new(SidebarKind.Project, label, label, 1, key);

    private readonly List<string> _ran = [];

    private IReadOnlyList<NotifierCommand> Menu(params SidebarNode[] recent)
        => MainForm.TrayCommands(
            recent,
            () => _ran.Add("open"),
            () => _ran.Add("quick add"),
            () => _ran.Add("sync"),
            () => _ran.Add("settings"),
            () => _ran.Add("updates"),
            () => _ran.Add("exit"),
            key => _ran.Add("go to " + key));

    private static string[] Labels(IReadOnlyList<NotifierCommand> menu)
        => menu.Select(c => c.IsRule ? "—" : c.Label).ToArray();

    [WinFormsFact]
    public void With_nowhere_to_go_back_to_the_menu_is_what_it_always_was()
    {
        // No empty group, and no rule with nothing under it, before a view has been opened.
        Assert.Equal(
            ["Open Termyn", "Quick add…", "—", "Sync now", "Settings…", "Check for updates…", "Exit"],
            Labels(Menu()));
    }

    [WinFormsFact]
    public void The_views_sit_in_a_group_of_their_own_under_quick_add()
    {
        var menu = Menu(View("Work", "project:p1"), View("Home", "project:p2"));

        Assert.Equal(
            ["Open Termyn", "Quick add…", "—", "Work", "Home", "—", "Sync now", "Settings…", "Check for updates…", "Exit"],
            Labels(menu));
    }

    [WinFormsFact]
    public void Picking_a_view_opens_that_view_and_not_the_one_beside_it()
    {
        // Each entry has to hold its own key. Built in a loop, they can all end up holding the last
        // one — which is a bug you only see by clicking the wrong entry.
        var menu = Menu(View("Work", "project:p1"), View("Home", "project:p2"), View("Admin", "project:p3"));

        foreach (var command in menu.Where(c => !c.IsRule && c.Label is "Work" or "Home" or "Admin"))
            command.Invoke();

        Assert.Equal(["go to project:p1", "go to project:p2", "go to project:p3"], _ran);
    }

    [WinFormsFact]
    public void Everything_else_still_does_what_it_says()
    {
        foreach (var command in Menu(View("Work", "project:p1")).Where(c => !c.IsRule && c.Label != "Work"))
            command.Invoke();

        Assert.Equal(["open", "quick add", "sync", "settings", "updates", "exit"], _ran);
    }

    [WinFormsFact]
    public void A_rule_does_nothing_when_a_desktop_lets_it_be_picked()
    {
        foreach (var rule in Menu(View("Work", "project:p1")).Where(c => c.IsRule))
            rule.Invoke();

        Assert.Empty(_ran);
    }
}
