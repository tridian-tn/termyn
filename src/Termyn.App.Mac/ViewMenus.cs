using ObjCRuntime;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The menus about how the tasks are laid out — Sort By and Columns — which turn up in three
/// places: the View menu, the toolbar's Sort button and a right-click on the table's header.
/// </summary>
/// <remarks>
/// Built fresh for each, since a menu item belongs to one menu at a time, but every copy sends the
/// same actions down the responder chain with the column in the tag. The window controller answers
/// them and ticks and greys each item as the menu opens, so the copies can't disagree.
///
/// Sort By is laid out as Mail lays out its own: Todoist's order, then the columns, then which way
/// they point. A header click on Windows only ever turns the current column round, and the menu is
/// where the direction can be named outright.
/// </remarks>
internal static class ViewMenus
{
    public const string ShowAsList = "showAsList:";
    public const string ShowAsTable = "showAsTable:";

    /// <summary>Sorts by the column in the item's tag, or puts Todoist's order back for none.</summary>
    public const string SortBy = "sortByColumn:";
    public const string SortAscending = "sortAscending:";
    public const string SortDescending = "sortDescending:";
    public const string ToggleColumn = "toggleTaskColumn:";
    public const string ShowAllColumns = "showAllTaskColumns:";
    public const string ExpandAll = "expandAllTasks:";
    public const string CollapseAll = "collapseAllTasks:";

    public static NSMenu SortByMenu(string title)
    {
        var menu = new NSMenu(title);
        menu.AddItem(Item(Strings.SortTodoistOrder, SortBy, TaskColumn.None));
        menu.AddItem(NSMenuItem.SeparatorItem);
        foreach (var column in TaskListController.Columns)
            menu.AddItem(Item(TaskListController.Heading(column), SortBy, column));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Item(Strings.SortAscending, SortAscending));
        menu.AddItem(Item(Strings.SortDescending, SortDescending));
        return menu;
    }

    public static NSMenu ColumnsMenu(string title)
    {
        var menu = new NSMenu(title);
        foreach (var column in TaskListController.Columns)
            menu.AddItem(Item(TaskListController.Heading(column), ToggleColumn, column));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Item(Strings.ShowAllColumns, ShowAllColumns));
        return menu;
    }

    /// <summary>The column an item is about, from its tag.</summary>
    public static TaskColumn ColumnOf(NSMenuItem item) => (TaskColumn)(int)item.Tag;

    private static NSMenuItem Item(string title, string action, TaskColumn column = TaskColumn.None)
        => new(title, new Selector(action), string.Empty) { Tag = (int)column };
}
