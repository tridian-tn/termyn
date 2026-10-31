using System.Globalization;
using CoreGraphics;
using ObjCRuntime;
using Termyn.Core;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The tasks in the selected view, as a single-column inset list the way Reminders lays one out.
/// </summary>
/// <remarks>
/// One column rather than the Windows app's grid: on a Mac a list of things to do reads as rows,
/// each with what it is and, under it in smaller type, when and where. Upcoming's days are group
/// rows, which float at the top while their tasks scroll under them.
///
/// The keys are the list's own, as Finder's are its own: Space ticks the selected task off, Return
/// renames it, ⌫ deletes it. Everything else goes through the menus, which the window validates.
/// </remarks>
internal sealed class TaskListController : NSViewController
{
    private readonly TaskCommands _commands;
    private readonly TaskTable _table;
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
        _table = new TaskTable(command => _commands.Run(command, SelectedId));
        _openFilter = NSButton.CreateButton(Strings.UnreadableFilterLink, OpenFilter);
        _emptyState = NSStackView.FromViews([_empty, _openFilter]);
        _bannerButton = NSButton.CreateButton(string.Empty, () => _bannerAction?.Invoke());
        _banner = Banner();
    }

    public override void LoadView()
    {
        var column = new NSTableColumn("Task") { ResizingMask = NSTableColumnResizing.Autoresizing };
        _table.AddColumn(column);
        _table.HeaderView = null;
        _table.Style = NSTableViewStyle.Inset;
        _table.UsesAutomaticRowHeights = true;
        _table.FloatsGroupRows = true;
        _table.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        _table.DataSource = new Source(this);
        _table.Delegate = new Delegate(this);
        _table.Menu = TaskMenu.Build(Strings.MenuTask);
        _table.Target = this;
        _table.DoubleAction = new Selector("renameClicked:");

        var scroll = new NSScrollView
        {
            DocumentView = _table,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        _empty.Alignment = NSTextAlignment.Center;
        _empty.TextColor = NSColor.SecondaryLabel;
        _empty.Font = NSFont.SystemFontOfSize(NSFont.SystemFontSize + 4)!;
        _empty.PreferredMaxLayoutWidth = 320;
        _emptyState.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        _emptyState.Spacing = 10;
        _emptyState.TranslatesAutoresizingMaskIntoConstraints = false;

        var column2 = NSStackView.FromViews([_banner, scroll]);
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
            scroll.WidthAnchor.ConstraintEqualTo(column2.WidthAnchor),
            _emptyState.CenterXAnchor.ConstraintEqualTo(scroll.CenterXAnchor),
            _emptyState.CenterYAnchor.ConstraintEqualTo(scroll.CenterYAnchor),
            _emptyState.WidthAnchor.ConstraintLessThanOrEqualTo(scroll.WidthAnchor, 1, -40),
        ]);

        View = view;
    }

    /// <summary>The list the view gets keyboard focus on when the window opens.</summary>
    public NSView FocusView => _table;

    /// <summary>The selected task, or null when nothing is or a day's heading somehow is.</summary>
    public string? SelectedId
        => _table.SelectedRow is var index && index >= 0 && index < _rows.Count && !_rows[(int)index].IsHeading
            ? _rows[(int)index].Id
            : null;

    /// <summary>Where a popover about a task points from: its row, in the list's own coordinates.</summary>
    public (NSView View, CGRect Rect)? AnchorFor(string id)
        => IndexOf(id) is { } row ? (_table, _table.RectForRow(row)) : null;

    /// <summary>
    /// Shows the presenter's rows, keeping the same task selected across a refresh — or, when it's
    /// gone, the one that came after it, the way Finder moves on from a file sent to the Trash.
    /// </summary>
    public void Render(
        IReadOnlyList<TaskRow> rows,
        IReadOnlyDictionary<string, Rgb> labelColours,
        UnreadableFilter? unreadable,
        DateOnly today)
    {
        if (_renaming is not null)
        {
            _heldBack = () => Render(rows, labelColours, unreadable, today);
            return;
        }

        var index = (int)_table.SelectedRow;
        var selected = SelectedId;

        _rows = rows;
        _labelColours = labelColours;
        _today = today;
        _table.ReloadData();

        if (selected is not null)
        {
            if (IndexOf(selected) is { } again)
                _table.SelectRow(again, byExtendingSelection: false);
            else if (Nearest(index) is { } next)
                _table.SelectRow(next, byExtendingSelection: false);
        }

        _filterLink = unreadable is null ? null : Links.Openable(unreadable.Link);
        _empty.StringValue = unreadable is null
            ? Strings.NoTasks
            : string.Format(CultureInfo.CurrentCulture, Strings.UnreadableFilter, unreadable.Query);
        _openFilter.Hidden = _filterLink is null;
        _emptyState.Hidden = rows.Count > 0;
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
        if (_table.ClickedRow >= 0 && _table.ClickedRow < _rows.Count && !_rows[(int)_table.ClickedRow].IsHeading)
            _commands.Run(AppCommand.Rename, _rows[(int)_table.ClickedRow].Id);
    }

    /// <summary>Opens a task's name for editing on its row, and renames it with what's typed.</summary>
    private void BeginRename(string id)
    {
        if (IndexOf(id) is not { } row)
            return;

        _table.SelectRow(row, byExtendingSelection: false);
        _table.ScrollRowToVisible(row);
        if (_table.GetView(0, row, makeIfNecessary: true) is not TaskCell cell)
            return;

        var opened = _rows[row].Content;
        _renaming = id;

        cell.BeginRename(text =>
        {
            _renaming = null;

            // Blank or unchanged is no rename. Either way the list goes back to taking keys.
            if (!string.IsNullOrWhiteSpace(text) && text != opened)
                _commands.Rename(id, text);

            View.Window?.MakeFirstResponder(_table);

            if (_heldBack is { } held)
            {
                _heldBack = null;
                held();
            }
        });
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
    private sealed class TaskTable(Action<AppCommand> run) : NSTableView
    {
        private const string Space = " ";
        private const string Return = "\r";
        private const string Backspace = "\u007f";

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
            else
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
                ?? new TaskCell(TaskId) { Toggle = id => owner._commands.Run(AppCommand.ToggleComplete, id) };
            cell.Show(task, owner._labelColours, owner._today);
            return cell;
        }

        /// <summary>What typing a few letters over the list matches against, as Finder's lists do.</summary>
        public override string GetSelectString(NSTableView tableView, NSTableColumn tableColumn, nint row)
            => owner._rows[(int)row].Content;
    }
}
