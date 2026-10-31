using ObjCRuntime;

namespace Termyn.App.Mac;

/// <summary>
/// The menu bar, laid out the way the Human Interface Guidelines lay out a Mac app's.
/// </summary>
/// <remarks>
/// Every item names an action rather than a target, so it goes down the responder chain: the
/// standard ones land on AppKit's own handlers, and AppKit enables and disables them as the front
/// window can or can't take them. The window controller answers the app's own — the views and
/// Sync Now — and while no window is open they're disabled the same way.
/// </remarks>
internal static class MainMenu
{
    public const string ShowToday = "showToday:";
    public const string ShowUpcoming = "showUpcoming:";
    public const string ShowInbox = "showInbox:";
    public const string SyncNow = "syncNow:";
    public const string OpenProjectPage = "openProjectPage:";
    public const string Reconnect = "reconnect:";

    public static NSMenu Build()
    {
        var app = NSApplication.SharedApplication;
        var bar = new NSMenu();

        var services = new NSMenu(Strings.MenuServices);
        app.ServicesMenu = services;

        // The first menu is titled with the app's name by macOS, whatever it's given here.
        Add(bar, "Termyn",
            Item(Strings.MenuAbout, "orderFrontStandardAboutPanel:"),
            NSMenuItem.SeparatorItem,

            // No Settings window yet. An item with no action is disabled, which says it'll be here
            // without pretending it does anything.
            new NSMenuItem(Strings.MenuSettings, ",") { KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask },

            // Account matters sit with Settings, as they do in Mail. Enabled only while Todoist
            // has turned the token away.
            Item(Strings.MenuReconnect, Reconnect),
            NSMenuItem.SeparatorItem,
            new NSMenuItem(Strings.MenuServices) { Submenu = services },
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuHide, "hide:", "h"),
            Item(Strings.MenuHideOthers, "hideOtherApplications:", "h", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask),
            Item(Strings.MenuShowAll, "unhideAllApplications:"),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuQuit, "terminate:", "q"));

        // New Task runs through the same path as the Task menu, so it's validated the same way:
        // greyed while Todoist has turned the token away.
        var newTask = Item(Strings.MenuNewTask, TaskMenu.Action, "n");
        newTask.Tag = (int)Presentation.AppCommand.NewTask;

        Add(bar, Strings.MenuFile,
            newTask,
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuClose, "performClose:", "w"));

        Add(bar, Strings.MenuEdit,
            Item(Strings.MenuUndo, "undo:", "z"),
            Item(Strings.MenuRedo, "redo:", "Z"),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuCut, "cut:", "x"),
            Item(Strings.MenuCopy, "copy:", "c"),
            Item(Strings.MenuPaste, "paste:", "v"),
            Item(Strings.MenuSelectAll, "selectAll:", "a"));

        // ⌘1–3 for the views, as Mail and Reminders number theirs, and ⌃⌘S for the sidebar, which
        // is the shortcut every sidebar on macOS answers to. AppKit retitles the sidebar item Hide
        // Sidebar while the sidebar is showing, and adds Enter Full Screen to this menu itself.
        //
        // As List and as Table are Finder's words for the same choice. Finder has ⌘1–4 for its view
        // styles, which here are the views themselves, so they take ⌃⌘ — the sidebar's modifiers.
        // Expand All and Collapse All have no keys: → and ← do a task at a time, as in Finder.
        Add(bar, Strings.MenuView,
            Item(Strings.MenuToday, ShowToday, "1"),
            Item(Strings.MenuUpcoming, ShowUpcoming, "2"),
            Item(Strings.MenuInbox, ShowInbox, "3"),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuAsList, ViewMenus.ShowAsList, "1", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask),
            Item(Strings.MenuAsTable, ViewMenus.ShowAsTable, "2", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask),
            NSMenuItem.SeparatorItem,
            new NSMenuItem(Strings.MenuSortBy) { Submenu = ViewMenus.SortByMenu(Strings.MenuSortBy) },
            new NSMenuItem(Strings.MenuColumns) { Submenu = ViewMenus.ColumnsMenu(Strings.MenuColumns) },
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuExpandAll, ViewMenus.ExpandAll),
            Item(Strings.MenuCollapseAll, ViewMenus.CollapseAll),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuSyncNow, SyncNow, "r"),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuShowSidebar, "toggleSidebar:", "s", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask));

        // What can be done to the selected task, between View and Window as an app's own menus are.
        var task = TaskMenu.Build(Strings.MenuTask);
        bar.AddItem(new NSMenuItem(task.Title) { Submenu = task });

        var window = Add(bar, Strings.MenuWindow,
            Item(Strings.MenuMinimize, "performMiniaturize:", "m"),
            Item(Strings.MenuZoom, "performZoom:"),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuBringAllToFront, "arrangeInFront:"));
        app.WindowsMenu = window;

        // Named as the Help menu so macOS adds its search field to the top of it.
        var help = Add(bar, Strings.MenuHelp,
            Item(Strings.MenuProjectPage, OpenProjectPage));
        app.HelpMenu = help;

        return bar;
    }

    private static NSMenu Add(NSMenu bar, string title, params NSMenuItem[] items)
    {
        var menu = new NSMenu(title);
        foreach (var item in items)
            menu.AddItem(item);

        bar.AddItem(new NSMenuItem(title) { Submenu = menu });
        return menu;
    }

    private static NSMenuItem Item(
        string title,
        string action,
        string key = "",
        NSEventModifierMask modifiers = NSEventModifierMask.CommandKeyMask)
        => new(title, new Selector(action), key) { KeyEquivalentModifierMask = modifiers };
}
