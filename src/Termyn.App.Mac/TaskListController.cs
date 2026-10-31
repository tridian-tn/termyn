using System.Globalization;
using CoreGraphics;
using ObjCRuntime;
using Termyn.Core;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>How the tasks are laid out: as Reminders lists them, or as Finder's list view tables files.</summary>
internal enum TaskViewStyle
{
    List,
    Table,
}

/// <summary>
/// The tasks in the selected view, as a single-column inset list the way Reminders lays one out,
/// or as a table with a column for each thing about a task, the way Finder lays out a folder.
/// </summary>
/// <remarks>
/// The list reads as rows, each with what it is and, under it in smaller type, when and where. The
/// table is the Windows app's grid in the Mac's own form: a header to click to sort by, and to
/// right-click to choose its columns, with AppKit remembering their widths, order and which are
/// showing. Each is its own table, and only the one in use is shown; keeping the two apart means
/// the list's one column is never saved as the table having lost the rest. In both, Upcoming's days
/// are group rows, which float at the top while their tasks scroll under them.
///
/// The order is the presenter's, as it is on Windows: a header click goes to it, and the header's
/// arrow is drawn from what it says. AppKit's own sort descriptors aren't used, so there's one
/// place the sort is kept rather than two to keep in step.
///
/// The keys are the list's own, as Finder's are its own: Space ticks the selected task off, Return
/// renames it, ⌫ deletes it, and → and ← open and fold a task's sub-tasks. Everything else goes
/// through the menus, which the window validates.
/// </remarks>
internal sealed class TaskListController : NSViewController
{
    /// <summary>Where the choice of list or table is kept between launches.</summary>
    private const string StyleKey = "TaskViewStyle";

    /// <summary>The table's columns, in the order they start in. Task is always first.</summary>
    public static readonly IReadOnlyList<TaskColumn> Columns =
    [
        TaskColumn.Content,
        TaskColumn.Priority,
        TaskColumn.Project,
        TaskColumn.Due,
        TaskColumn.Deadline,
        TaskColumn.Labels,
    ];

    private readonly TaskCommands _commands;
    private readonly TaskTable _list;
    private readonly TaskTable _grid;
    private readonly NSView _body = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private NSScrollView? _listScroll;
    private NSScrollView? _gridScroll;
    private TaskViewStyle _style;
    private readonly NSTextField _empty = NSTextField.CreateWrappingLabel(string.Empty);
    private readonly NSButton _openFilter;
    private readonly NSStackView _emptyState;
    private readonly NSStackView _banner;
    private readonly NSImageView _bannerIcon = new();
    private readonly NSTextField _bannerText = NSTextField.CreateWrappingLabel(string.Empty);
    private readonly NSButton _bannerButton;

    private IReadOnlyList<TaskRow> _rows = [];
    private IReadOnlyDictionary<string, Rgb> _labelColours = new Dictionary<string, Rgb>();
    private DateOnly _today;
    private string? _filterLink;
    private Action? _bannerAction;

    /// <summary>
    /// The task whose name is open for editing, or null. While it's set nothing reloads the list:
    /// a background sync landing mid-rename would otherwise throw the editor away, and the words in
    /// it — the same care the Windows outline takes.
    /// </summary>
    private string? _renaming;

    /// <summary>A render that arrived while a rename was open, to be shown when it closes.</summary>
    private Action? _heldBack;

    public TaskListController(TaskCommands commands)
    {
        _commands = commands;
        _commands.BeginRename = BeginRename;
        _list = new TaskTable(command => _commands.Run(command, SelectedId), Arrow);
        _grid = new TaskTable(command => _commands.Run(command, SelectedId), Arrow);
        _style = NSUserDefaults.StandardUserDefaults.StringForKey(StyleKey) == nameof(TaskViewStyle.Table)
            ? TaskViewStyle.Table
            : TaskViewStyle.List;
        _openFilter = NSButton.CreateButton(Strings.UnreadableFilterLink, OpenFilter);
        _emptyState = NSStackView.FromViews([_empty, _openFilter]);
        _bannerButton = NSButton.CreateButton(string.Empty, () => _bannerAction?.Invoke());
        _banner = Banner();
    }

    public override void LoadView()
    {
        var column = new NSTableColumn("Task") { ResizingMask = NSTableColumnResizing.Autoresizing };
        _list.AddColumn(column);
        _list.HeaderView = null;
        _list.Style = NSTableViewStyle.Inset;
        _list.UsesAutomaticRowHeights = true;
        _list.FloatsGroupRows = true;
        _list.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        _list.DataSource = new Source(this);
        _list.Delegate = new Delegate(this);
        _list.Menu = TaskMenu.Build(Strings.MenuTask);
        _list.Target = this;
        _list.DoubleAction = new Selector("renameClicked:");

        LoadGrid();

        // Both kept in the one place, one hidden, rather than in the stack: a stack takes a hidden
        // view out of the window altogether, and the constraints that sized it go with it.
        _listScroll = Scroll(_list);
        _gridScroll = Scroll(_grid);

        // Columns wider between them than the window scroll sideways, as Finder's do.
        _gridScroll.HasHorizontalScroller = true;
        foreach (var each in new[] { _listScroll, _gridScroll })
        {
            _body.AddSubview(each);
            NSLayoutConstraint.ActivateConstraints(
            [
                each.TopAnchor.ConstraintEqualTo(_body.TopAnchor),
                each.BottomAnchor.ConstraintEqualTo(_body.BottomAnchor),
                each.LeadingAnchor.ConstraintEqualTo(_body.LeadingAnchor),
                each.TrailingAnchor.ConstraintEqualTo(_body.TrailingAnchor),
            ]);
        }

        ShowStyle();

        _empty.Alignment = NSTextAlignment.Center;
        _empty.TextColor = NSColor.SecondaryLabel;
        _empty.Font = NSFont.SystemFontOfSize(NSFont.SystemFontSize + 4)!;
        _empty.PreferredMaxLayoutWidth = 320;
        _emptyState.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        _emptyState.Spacing = 10;
        _emptyState.TranslatesAutoresizingMaskIntoConstraints = false;

        var column2 = NSStackView.FromViews([_banner, _body]);
        column2.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column2.Spacing = 0;
        column2.TranslatesAutoresizingMaskIntoConstraints = false;

        var view = new NSView();
        view.AddSubview(column2);
        view.AddSubview(_emptyState);

        NSLayoutConstraint.ActivateConstraints(
        [
            column2.TopAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.TopAnchor),
            column2.BottomAnchor.ConstraintEqualTo(view.BottomAnchor),
            column2.LeadingAnchor.ConstraintEqualTo(view.LeadingAnchor),
            column2.TrailingAnchor.ConstraintEqualTo(view.TrailingAnchor),
            _banner.WidthAnchor.ConstraintEqualTo(column2.WidthAnchor),
            _body.WidthAnchor.ConstraintEqualTo(column2.WidthAnchor),
            _emptyState.CenterXAnchor.ConstraintEqualTo(_body.CenterXAnchor),
            _emptyState.CenterYAnchor.ConstraintEqualTo(_body.CenterYAnchor),
            _emptyState.WidthAnchor.ConstraintLessThanOrEqualTo(_body.WidthAnchor, 1, -40),
        ]);

        View = view;
    }

    /// <summary>
    /// The table: a column for each thing about a task, as the Windows outline has, with a header
    /// that sorts on a click and offers its columns on a right-click.
    /// </summary>
    /// <remarks>
    /// The Task column stays first and takes up whatever the window's width leaves of the others,
    /// as Finder's Name column does; the rest can be resized, dragged into another order, or hidden,
    /// and AppKit keeps all of that by the table's autosave name. The widths it starts at are the
    /// Windows outline's.
    /// </remarks>
    private void LoadGrid()
    {
        foreach (var each in Columns)
        {
            var column = new NSTableColumn(each.ToString())
            {
                Width = StartupLayout.ColumnWidth(each),
                MinWidth = each switch
                {
                    TaskColumn.Content => 160,
                    TaskColumn.Priority => 28,
                    _ => 60,
                },
                ResizingMask = each == TaskColumn.Content
                    ? NSTableColumnResizing.Autoresizing | NSTableColumnResizing.UserResizingMask
                    : NSTableColumnResizing.UserResizingMask,
                HeaderToolTip = Heading(each),
            };

            // A flag over the priority column, as Mail heads its flag column, rather than a word
            // the column's too narrow to hold.
            if (each == TaskColumn.Priority)
            {
                column.MaxWidth = 60;
                column.HeaderCell.Alignment = NSTextAlignment.Center;
                column.HeaderCell.AttributedStringValue = NSAttributedString.FromAttachment(
                    new NSTextAttachment { Image = NSImage.GetSystemSymbol("flag", Heading(each)) });
            }
            else
            {
                column.Title = Heading(each);
            }

            _grid.AddColumn(column);
        }

        _grid.Style = NSTableViewStyle.Inset;
        _grid.UsesAlternatingRowBackgroundColors = true;
        _grid.UsesAutomaticRowHeights = false;
        _grid.FloatsGroupRows = true;
        _grid.AllowsColumnReordering = true;
        _grid.AllowsColumnResizing = true;
        _grid.AllowsColumnSelection = false;
        _grid.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        _grid.DataSource = new Source(this);
        _grid.Delegate = new GridDelegate(this);
        _grid.Menu = TaskMenu.Build(Strings.MenuTask);
        _grid.HeaderView!.Menu = ViewMenus.ColumnsMenu(Strings.MenuColumns);
        _grid.Target = this;
        _grid.DoubleAction = new Selector("renameClicked:");

        // Last, once the columns are there for it to put back as they were.
        _grid.AutosaveName = "TaskColumns";
        _grid.AutosaveTableColumns = true;
    }

    private static NSScrollView Scroll(NSTableView table) => new()
    {
        DocumentView = table,
        HasVerticalScroller = true,
        AutohidesScrollers = true,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    /// <summary>A column's name in its header and in the menus that list the columns.</summary>
    public static string Heading(TaskColumn column) => column switch
    {
        TaskColumn.Content => Strings.ColumnTask,
        TaskColumn.Priority => Strings.ColumnPriority,
        TaskColumn.Project => Strings.ColumnProject,
        TaskColumn.Due => Strings.ColumnDue,
        TaskColumn.Deadline => Strings.ColumnDeadline,
        TaskColumn.Labels => Strings.ColumnLabels,
        _ => string.Empty,
    };

    /// <summary>The table in use: the list or the grid.</summary>
    private TaskTable Current => _style == TaskViewStyle.Table ? _grid : _list;

    /// <summary>The list the view gets keyboard focus on when the window opens.</summary>
    public NSView FocusView => Current;

    /// <summary>Whether the tasks are shown as a list or as a table.</summary>
    public TaskViewStyle Style => _style;

    /// <summary>
    /// Shows the tasks the other way, on the same task as before, and remembers the choice for the
    /// next launch — as Finder keeps a window's view style.
    /// </summary>
    public void SetStyle(TaskViewStyle style)
    {
        // Not while a name is open for editing: the field is in the table that would be hidden.
        if (style == _style || _renaming is not null)
            return;

        var selected = SelectedId;
        var focused = View.Window?.FirstResponder == Current;

        _style = style;
        NSUserDefaults.StandardUserDefaults.SetString(style.ToString(), StyleKey);
        ShowStyle();

        if (selected is not null && IndexOf(selected) is { } row)
            Select(row);
        if (focused)
            View.Window?.MakeFirstResponder(Current);
    }

    private void ShowStyle()
    {
        _listScroll!.Hidden = _style != TaskViewStyle.List;
        _gridScroll!.Hidden = _style != TaskViewStyle.Table;
    }

    /// <summary>Whether a column of the table is showing.</summary>
    public bool IsShown(TaskColumn column) => Column(column) is { Hidden: false };

    /// <summary>Whether every column of the table is showing.</summary>
    public bool AllShown => Columns.All(IsShown);

    /// <summary>Hides a column of the table, or shows it again. The Task column stays.</summary>
    public void ToggleColumn(TaskColumn column)
    {
        if (column != TaskColumn.Content && Column(column) is { } found)
            found.Hidden = !found.Hidden;
    }

    /// <summary>Shows every column of the table again.</summary>
    public void ShowAllColumns()
    {
        foreach (var column in _grid.TableColumns())
            column.Hidden = false;
    }

    private NSTableColumn? Column(TaskColumn column) => _grid.FindTableColumn(new NSString(column.ToString()));

    /// <summary>
    /// Hides or shows what is filed under a task, leaving the selection somewhere it can be seen.
    /// </summary>
    /// <remarks>
    /// Folding over the selected task would take its row off the list. The task that was folded is
    /// what the user was pointing at, so that is where the selection goes, as on Windows. It's
    /// moved first rather than after: the rows come back on the next turn of the main thread, by
    /// which time the row it was on has gone.
    /// </remarks>
    /// <param name="id">The task whose sub-tasks are being hidden or shown</param>
    /// <param name="collapsed">True to hide them, false to show them again</param>
    public void Fold(string id, bool collapsed)
    {
        if (collapsed && IndexOf(id) is { } parent && Current.SelectedRow is var selected && Under(parent, (int)selected))
            Select(parent);

        _commands.Fold(id, collapsed);
    }

    /// <summary>
    /// Folds or unfolds every task in the view, leaving the selection on the task at the top of
    /// the selected one's branch, which is the one still on screen once everything's folded.
    /// </summary>
    public void FoldAll(bool collapsed)
    {
        if (collapsed && Current.SelectedRow is var selected && selected >= 0 && selected < _rows.Count)
        {
            var top = (int)selected;
            while (_rows[top].Depth > 0 && Parent(top) is { } up)
                top = up;
            Select(top);
        }

        _commands.FoldAll(collapsed);
    }

    /// <summary>The selected task, or null when nothing is or a day's heading somehow is.</summary>
    public string? SelectedId
        => Current.SelectedRow is var index && index >= 0 && index < _rows.Count && !_rows[(int)index].IsHeading
            ? _rows[(int)index].Id
            : null;

    /// <summary>Where a popover about a task points from: its row, in the list's own coordinates.</summary>
    public (NSView View, CGRect Rect)? AnchorFor(string id)
        => IndexOf(id) is { } row ? (Current, Current.RectForRow(row)) : null;

    /// <summary>
    /// Shows the presenter's rows, keeping the same task selected across a refresh — or, when it's
    /// gone, the one that came after it, the way Finder moves on from a file sent to the Trash.
    /// </summary>
    public void Render(
        IReadOnlyList<TaskRow> rows,
        IReadOnlyDictionary<string, Rgb> labelColours,
        UnreadableFilter? unreadable,
        DateOnly today,
        TaskSort sort)
    {
        if (_renaming is not null)
        {
            _heldBack = () => Render(rows, labelColours, unreadable, today, sort);
            return;
        }

        var index = (int)Current.SelectedRow;
        var selected = SelectedId;

        _rows = rows;
        _labelColours = labelColours;
        _today = today;

        // Both, so the one out of sight never holds rows the data source no longer has.
        _list.ReloadData();
        _grid.ReloadData();
        ShowSort(sort);

        if (selected is not null)
        {
            if (IndexOf(selected) is { } again)
                Current.SelectRow(again, byExtendingSelection: false);
            else if (Nearest(index) is { } next)
                Current.SelectRow(next, byExtendingSelection: false);
        }

        _filterLink = unreadable is null ? null : Links.Openable(unreadable.Link);
        _empty.StringValue = unreadable is null
            ? Strings.NoTasks
            : string.Format(CultureInfo.CurrentCulture, Strings.UnreadableFilter, unreadable.Query);
        _openFilter.Hidden = _filterLink is null;
        _emptyState.Hidden = rows.Count > 0;
    }

    /// <summary>
    /// Marks the column the table is sorted by with the arrow that says which way, and highlights
    /// its header, as Finder marks the column a folder is sorted by. No column is marked while the
    /// tasks are in Todoist's own order.
    /// </summary>
    private void ShowSort(TaskSort sort)
    {
        var arrow = NSImage.ImageNamed(sort.Descending ? "NSDescendingSortIndicator" : "NSAscendingSortIndicator");
        NSTableColumn? sorted = null;

        foreach (var column in _grid.TableColumns())
        {
            var mine = !sort.IsDefault && column.Identifier == sort.Column.ToString();
            _grid.SetIndicatorImage(mine ? arrow : null, column);
            if (mine)
                sorted = column;
        }

        // Nil is AppKit's own "no column", which the binding doesn't say it takes.
        _grid.HighlightedTableColumn = sorted!;
    }

    /// <summary>
    /// Shows a strip across the top of the list, or hides it.
    /// </summary>
    /// <param name="text">What it says, or null to hide it</param>
    /// <param name="button">The button at its end, or null for none</param>
    /// <param name="action">What the button does</param>
    public void ShowBanner(string? text, string? button = null, Action? action = null)
    {
        _banner.Hidden = text is null;
        _bannerText.StringValue = text ?? string.Empty;
        _bannerButton.Title = button ?? string.Empty;
        _bannerButton.Hidden = button is null;
        _bannerAction = action;
    }

    /// <summary>⌘Z over the list takes back the last tick or delete, as the Windows outline's Ctrl+Z does.</summary>
    /// <remarks>
    /// Answered here rather than by the window, which would hand it to its own undo manager first.
    /// A text field being typed in is ahead of this in the chain, so ⌘Z there still undoes typing.
    /// </remarks>
    [Export("undo:")]
    private void Undo(NSObject sender) => _commands.Run(AppCommand.Undo, null);

    [Export("redo:")]
    private void Redo(NSObject sender)
    {
        // Nothing: there's no redo below the window, and validation keeps this greyed.
    }

    [Export("validateMenuItem:")]
    private bool ValidateMenuItem(NSMenuItem item) => item.Action?.Name switch
    {
        "undo:" => _commands.State(AppCommand.Undo, null).Enabled,
        "redo:" => false,
        _ => true,
    };

    [Export("renameClicked:")]
    private void RenameClicked(NSObject sender)
    {
        var clicked = Current.ClickedRow;
        if (clicked >= 0 && clicked < _rows.Count && !_rows[(int)clicked].IsHeading)
            _commands.Run(AppCommand.Rename, _rows[(int)clicked].Id);
    }

    /// <summary>Opens a task's name for editing on its row, and renames it with what's typed.</summary>
    private void BeginRename(string id)
    {
        if (IndexOf(id) is not { } row)
            return;

        Select(row);

        // The name's column: the list's only one, or wherever the table's Task column stands.
        var column = _style == TaskViewStyle.Table ? _grid.FindColumn(new NSString(nameof(TaskColumn.Content))) : 0;
        if (column < 0 || Current.GetView(column, row, makeIfNecessary: true) is not TaskCell cell)
            return;

        var opened = _rows[row].Content;
        _renaming = id;

        cell.BeginRename(text =>
        {
            _renaming = null;

            // Blank or unchanged is no rename. Either way the list goes back to taking keys.
            if (!string.IsNullOrWhiteSpace(text) && text != opened)
                _commands.Rename(id, text);

            View.Window?.MakeFirstResponder(Current);

            if (_heldBack is { } held)
            {
                _heldBack = null;
                held();
            }
        });
    }

    /// <summary>
    /// What → and ← do on the selected task: open or fold its sub-tasks, and ← on one that has
    /// nothing open goes up to the task it's filed under, as it does in Finder.
    /// </summary>
    /// <returns>True when a task was selected for the arrow to act on</returns>
    private bool Arrow(bool left)
    {
        var index = (int)Current.SelectedRow;
        if (index < 0 || index >= _rows.Count || _rows[index].IsHeading)
            return false;

        var row = _rows[index];
        if (row.HasChildren && row.Collapsed != left)
            Fold(row.Id, collapsed: left);
        else if (left && Parent(index) is { } parent)
            Select(parent);

        return true;
    }

    private void Select(int row)
    {
        Current.SelectRow(row, byExtendingSelection: false);
        Current.ScrollRowToVisible(row);
    }

    /// <summary>The row of the task a row's task is filed under, or null for one at the top.</summary>
    private int? Parent(int index)
    {
        var depth = _rows[index].Depth;
        for (var i = index - 1; i >= 0 && depth > 0; i--)
        {
            if (_rows[i].IsHeading)
                return null;
            if (_rows[i].Depth < depth)
                return i;
        }

        return null;
    }

    /// <summary>Whether a row is one of those filed, at any depth, under the task on another.</summary>
    private bool Under(int parent, int row)
    {
        if (row <= parent || row >= _rows.Count)
            return false;

        for (var i = parent + 1; i <= row; i++)
        {
            if (_rows[i].IsHeading || _rows[i].Depth <= _rows[parent].Depth)
                return false;
        }

        return true;
    }

    private int? IndexOf(string id)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Id == id && !_rows[i].IsHeading)
                return i;
        }

        return null;
    }

    /// <summary>The task at or after a place in the list, or the last one before it.</summary>
    private int? Nearest(int index)
    {
        for (var i = Math.Max(index, 0); i < _rows.Count; i++)
        {
            if (!_rows[i].IsHeading)
                return i;
        }

        for (var i = Math.Min(index, _rows.Count) - 1; i >= 0; i--)
        {
            if (!_rows[i].IsHeading)
                return i;
        }

        return null;
    }

    private void OpenFilter()
    {
        if (_filterLink is not null)
            NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(_filterLink));
    }

    /// <summary>
    /// A strip across the top of the list for what the user ought to know — a token Todoist has
    /// turned away, changes it refused — in the system's warning colour. The tasks under it are
    /// still the local copy, and still worth reading.
    /// </summary>
    private NSStackView Banner()
    {
        _bannerIcon.Image = NSImage.GetSystemSymbol("exclamationmark.triangle.fill", null);
        _bannerIcon.ContentTintColor = NSColor.SystemYellow;
        _bannerIcon.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);

        _bannerText.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        _bannerText.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _bannerButton.ControlSize = NSControlSize.Small;
        _bannerButton.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);

        var strip = NSStackView.FromViews([_bannerIcon, _bannerText, _bannerButton]);
        strip.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        strip.Spacing = 8;
        strip.EdgeInsets = new NSEdgeInsets(8, 16, 8, 16);
        strip.WantsLayer = true;
        strip.Layer!.BackgroundColor = NSColor.SystemYellow.ColorWithAlphaComponent(0.15f).CGColor;
        strip.Hidden = true;
        return strip;
    }

    /// <summary>
    /// The list's own keys, and a right-click that acts on the row clicked rather than whatever
    /// happened to be selected.
    /// </summary>
    private sealed class TaskTable(Action<AppCommand> run, Func<bool, bool> arrow) : NSTableView
    {
        private const string Space = " ";
        private const string Return = "\r";
        private const string Backspace = "\u007f";
        private const string Left = "\uF702";
        private const string Right = "\uF703";

        public override void KeyDown(NSEvent theEvent)
        {
            var plain = (theEvent.ModifierFlags & (NSEventModifierMask.CommandKeyMask
                | NSEventModifierMask.ControlKeyMask | NSEventModifierMask.AlternateKeyMask)) == 0;

            var command = plain && SelectedRow >= 0
                ? theEvent.CharactersIgnoringModifiers switch
                {
                    Space => AppCommand.ToggleComplete,
                    Return => AppCommand.Rename,
                    Backspace => AppCommand.Delete,
                    _ => (AppCommand?)null,
                }
                : null;

            if (command is { } run1)
                run(run1);
            else if (!(plain && theEvent.CharactersIgnoringModifiers is Left or Right && arrow(theEvent.CharactersIgnoringModifiers == Left)))
                base.KeyDown(theEvent);
        }

        /// <summary>Selects the row under the pointer first, as Mail and Finder do.</summary>
        public override NSMenu? MenuForEvent(NSEvent theEvent)
        {
            var row = GetRow(ConvertPointFromView(theEvent.LocationInWindow, null));
            if (row >= 0 && !IsRowSelected(row))
                SelectRow(row, byExtendingSelection: false);

            return row >= 0 ? base.MenuForEvent(theEvent) : null;
        }
    }

    private sealed class Source(TaskListController owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._rows.Count;
    }

    private sealed class Delegate(TaskListController owner) : NSTableViewDelegate
    {
        private const string TaskId = "Task";
        private const string DayId = "Day";

        public override bool IsGroupRow(NSTableView tableView, nint row) => owner._rows[(int)row].IsHeading;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => !owner._rows[(int)row].IsHeading;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var task = owner._rows[(int)row];

            if (task.IsHeading)
            {
                var day = tableView.MakeView(DayId, owner) as NSTableCellView ?? TaskCell.Day(DayId);
                day.TextField!.StringValue = task.Content;
                return day;
            }

            var cell = tableView.MakeView(TaskId, owner) as TaskCell
                ?? new TaskCell(TaskId)
                {
                    Toggle = id => owner._commands.Run(AppCommand.ToggleComplete, id),
                    Fold = owner.Fold,
                };
            cell.Show(task, owner._labelColours, owner._today);
            return cell;
        }

        /// <summary>What typing a few letters over the list matches against, as Finder's lists do.</summary>
        public override string GetSelectString(NSTableView tableView, NSTableColumn tableColumn, nint row)
            => owner._rows[(int)row].Content;
    }

    /// <summary>The table's cells, a column at a time, and what its header does when it's clicked.</summary>
    private sealed class GridDelegate(TaskListController owner) : NSTableViewDelegate
    {
        private const string TaskId = "GridTask";
        private const string CellId = "GridCell";
        private const string DayId = "Day";

        /// <summary>A line apiece, as Finder's list rows are; a day's heading has room above it.</summary>
        private const int RowHeight = 24;
        private const int DayHeight = 30;

        public override bool IsGroupRow(NSTableView tableView, nint row) => owner._rows[(int)row].IsHeading;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => !owner._rows[(int)row].IsHeading;

        public override nfloat GetRowHeight(NSTableView tableView, nint row)
            => owner._rows[(int)row].IsHeading ? DayHeight : RowHeight;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var task = owner._rows[(int)row];

            // A day's heading spans the table, and is asked for once with no column.
            if (task.IsHeading)
            {
                if (tableColumn is not null)
                    return null!;

                var day = tableView.MakeView(DayId, owner) as NSTableCellView ?? TaskCell.Day(DayId);
                day.TextField!.StringValue = task.Content;
                return day;
            }

            if (tableColumn is null || !Enum.TryParse<TaskColumn>(tableColumn.Identifier, out var column))
                return null!;

            if (column == TaskColumn.Content)
            {
                var name = tableView.MakeView(TaskId, owner) as TaskCell
                    ?? new TaskCell(TaskId, compact: true)
                    {
                        Toggle = id => owner._commands.Run(AppCommand.ToggleComplete, id),
                        Fold = owner.Fold,
                    };
                name.Show(task, owner._labelColours, owner._today);
                return name;
            }

            var cell = tableView.MakeView(CellId, owner) as GridCell ?? new GridCell(CellId);
            cell.Show(task, column, owner._labelColours, owner._today);
            return cell;
        }

        /// <summary>
        /// Sorts by the column whose header was clicked: a first click from the top, a second the
        /// other way round, as on Windows. Todoist's own order is in View ▸ Sort By.
        /// </summary>
        public override void DidClickTableColumn(NSTableView tableView, NSTableColumn tableColumn)
        {
            if (Enum.TryParse<TaskColumn>(tableColumn.Identifier, out var column))
                owner._commands.SortBy(column);
        }

        /// <summary>The Task column stays first, as Finder's Name column does; the rest can move.</summary>
        public override bool ShouldReorder(NSTableView tableView, nint columnIndex, nint newColumnIndex)
            => columnIndex != 0 && newColumnIndex != 0;

        public override string GetSelectString(NSTableView tableView, NSTableColumn tableColumn, nint row)
            => owner._rows[(int)row].Content;
    }
}
