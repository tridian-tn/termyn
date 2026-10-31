using System.Globalization;
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
/// </remarks>
internal sealed class TaskListController : NSViewController
{
    private readonly NSTableView _table = new();
    private readonly NSTextField _empty = NSTextField.CreateWrappingLabel(string.Empty);
    private readonly NSButton _openFilter;
    private readonly NSStackView _emptyState;
    private readonly NSView _banner;

    private IReadOnlyList<TaskRow> _rows = [];
    private IReadOnlyDictionary<string, Rgb> _labelColours = new Dictionary<string, Rgb>();
    private DateOnly _today;
    private string? _filterLink;

    public TaskListController()
    {
        _openFilter = NSButton.CreateButton(Strings.UnreadableFilterLink, OpenFilter);
        _emptyState = NSStackView.FromViews([_empty, _openFilter]);
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

    /// <summary>Shows the presenter's rows, keeping the same task selected across a refresh.</summary>
    public void Render(
        IReadOnlyList<TaskRow> rows,
        IReadOnlyDictionary<string, Rgb> labelColours,
        UnreadableFilter? unreadable,
        DateOnly today)
    {
        var selected = _table.SelectedRow is var index && index >= 0 && index < _rows.Count ? _rows[(int)index].Id : null;

        _rows = rows;
        _labelColours = labelColours;
        _today = today;
        _table.ReloadData();

        if (selected is not null && IndexOf(selected) is { } again)
            _table.SelectRow(again, byExtendingSelection: false);

        _filterLink = unreadable is null ? null : Links.Openable(unreadable.Link);
        _empty.StringValue = unreadable is null
            ? Strings.NoTasks
            : string.Format(CultureInfo.CurrentCulture, Strings.UnreadableFilter, unreadable.Query);
        _openFilter.Hidden = _filterLink is null;
        _emptyState.Hidden = rows.Count > 0;
    }

    /// <summary>Shows or hides the notice that the saved token has been turned away.</summary>
    public void ShowReconnectNeeded(bool needed) => _banner.Hidden = !needed;

    private int? IndexOf(string id)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Id == id && !_rows[i].IsHeading)
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
    /// A strip across the top of the list, in the system's warning colour, for a token Todoist has
    /// turned away. The tasks under it are still the local copy, and still worth reading.
    /// </summary>
    private static NSView Banner()
    {
        var icon = NSImageView.FromImage(NSImage.GetSystemSymbol("exclamationmark.triangle.fill", null)!);
        icon.ContentTintColor = NSColor.SystemYellow;

        var text = NSTextField.CreateWrappingLabel(Strings.ReconnectNeeded);
        text.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;

        var strip = NSStackView.FromViews([icon, text]);
        strip.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        strip.Spacing = 8;
        strip.EdgeInsets = new NSEdgeInsets(8, 16, 8, 16);
        strip.WantsLayer = true;
        strip.Layer!.BackgroundColor = NSColor.SystemYellow.ColorWithAlphaComponent(0.15f).CGColor;
        strip.Hidden = true;
        return strip;
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

            var cell = tableView.MakeView(TaskId, owner) as TaskCell ?? new TaskCell(TaskId);
            cell.Show(task, owner._labelColours, owner._today);
            return cell;
        }

        /// <summary>What typing a few letters over the list matches against, as Finder's lists do.</summary>
        public override string GetSelectString(NSTableView tableView, NSTableColumn tableColumn, nint row)
            => owner._rows[(int)row].Content;
    }
}
