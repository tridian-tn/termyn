using Termyn.Core.Sync;
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
    private static RecentView View(string label, string key) => new(key, label);

    private readonly List<string> _ran = [];

    private IReadOnlyList<NotifierCommand> Menu(params RecentView[] recent)
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

    [WinFormsFact]
    public void A_view_with_no_name_is_still_an_entry_rather_than_a_rule()
    {
        // The labels are the account's now, and nothing promises a project has a name. Read off an
        // empty label, "this is a rule" would draw that project as a line and lose it.
        var menu = Menu(View(string.Empty, "project:p1"));

        // Two rules, because the group of views is there — and the nameless view is one of them.
        Assert.Equal(2, menu.Count(c => c.IsRule));
        Assert.Contains(menu, c => !c.IsRule && c.Label.Length == 0);
    }

    // ---- The window and the tray together --------------------------------------------------------

    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Home","child_order":2}""");
        return store;
    }

    [WinFormsFact]
    public void The_menu_is_filled_as_it_opens_rather_than_kept_up_to_date()
    {
        // Worked out at the moment it's shown, so it can't go stale and can't be rewritten under
        // the pointer of somebody reading it.
        using var window = TestWindow.Build("tray-open.json", Account(), out var tray, out var presenter);

        // Shown, off-screen: a form that was never displayed has no handle, and the window drops
        // work aimed at a window that isn't there.
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-2000, -2000);
        window.Show();

        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.Select(ViewSelection.OfProject("p2"));

        Assert.DoesNotContain(tray.Commands, c => c.Label == "Work");

        tray.Opening();

        Assert.Contains(tray.Commands, c => c.Label == "Work");
    }

    [WinFormsFact]
    public void Picking_a_view_opens_the_window_on_it()
    {
        using var window = TestWindow.Build("tray-pick.json", Account(), out var tray, out var presenter);

        // Shown, off-screen: a form that was never displayed has no handle, and the window drops
        // work aimed at a window that isn't there.
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-2000, -2000);
        window.Show();

        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.Select(ViewSelection.OfProject("p2"));
        tray.Opening();
        tray.Pick("Work");

        Assert.Equal(SidebarKeys.For(SidebarKind.Project, "p1"), presenter.SelectedKey);
        Assert.Equal(ViewSelection.OfProject("p1"), presenter.Selection);
    }

    [WinFormsFact]
    public void Picking_a_view_the_account_no_longer_has_still_opens_the_window()
    {
        // The menu is built as it opens, so this needs the row to go between opening the menu and
        // picking from it — but a stale entry mustn't leave the window unopened either way.
        using var window = TestWindow.Build("tray-gone.json", Account(), out var tray, out var presenter);

        // Shown, off-screen: a form that was never displayed has no handle, and the window drops
        // work aimed at a window that isn't there.
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-2000, -2000);
        window.Show();

        presenter.Select(ViewSelection.OfProject("p1"));
        presenter.Select(ViewSelection.OfProject("p2"));
        tray.Opening();

        var wasOn = presenter.SelectedKey;
        tray.Commands.First(c => c.Label == "Work").Invoke();

        Assert.NotEqual(wasOn, presenter.SelectedKey);
    }
}
