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
            NSMenuItem.SeparatorItem,
            new NSMenuItem(Strings.MenuServices) { Submenu = services },
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuHide, "hide:", "h"),
            Item(Strings.MenuHideOthers, "hideOtherApplications:", "h", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask),
            Item(Strings.MenuShowAll, "unhideAllApplications:"),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuQuit, "terminate:", "q"));

        Add(bar, Strings.MenuFile,
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
        Add(bar, Strings.MenuView,
            Item(Strings.MenuToday, ShowToday, "1"),
            Item(Strings.MenuUpcoming, ShowUpcoming, "2"),
            Item(Strings.MenuInbox, ShowInbox, "3"),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuSyncNow, SyncNow, "r"),
            NSMenuItem.SeparatorItem,
            Item(Strings.MenuShowSidebar, "toggleSidebar:", "s", NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask));

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
