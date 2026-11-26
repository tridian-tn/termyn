using System.Globalization;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The popover a task's labels are ticked in: a field to narrow them down or name a new one, over
/// the account's labels with a checkbox apiece.
/// </summary>
/// <remarks>
/// Labels are joined by name, so this deals in names throughout and hands back the whole set, as
/// the Windows picker does: Todoist has no way to add or remove just one. What's ticked is applied
/// when the popover closes, as Finder applies a file's tags, however it was closed. Esc is the one
/// way out that leaves the task as it was.
///
/// Return ticks the row the list is on and empties the field for the next one, or closes the
/// popover when it's on no row and there's nothing typed. What's typed that isn't a label yet gets
/// a row of its own at the end of the list, so a name that happens to contain another can still be
/// made. The list narrows the way Move To's does, by a few letters from anywhere in the name.
/// </remarks>
internal sealed class LabelsController(MainPresenter presenter, TaskCommands commands, string id, Action close) : SearchPopoverController(close)
{
    private const string CellId = "Label";

    private readonly List<string> _names = [];
    private readonly HashSet<string> _ticked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The labels the task had when the popover opened, kept in case its row goes meanwhile.</summary>
    private IReadOnlyList<string> _opened = [];

    /// <summary>The names the list is showing, narrowed by what's been typed.</summary>
    private List<string> _shown = [];

    /// <summary>What's been typed, when it isn't a label yet and so has the last row to itself.</summary>
    private string? _fresh;

    private bool _cancelled;

    protected override int RowCount => _shown.Count + (_fresh is null ? 0 : 1);

    public override void LoadView()
    {
        var row = commands.Row(id);
        _opened = row?.Labels ?? [];

        _names.AddRange(presenter.Labels.Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase));

        // A label the task wears that the account doesn't list — one left behind by a rename, say.
        // Dropping it silently would take it off the task the moment the popover closed.
        _names.AddRange(_opened.Where(a => !_names.Contains(a, StringComparer.OrdinalIgnoreCase)));
        _ticked.UnionWith(_opened);

        View = Lay(row?.Content ?? string.Empty, Strings.LabelsPlaceholder, new Source(this), new Delegate(this), 300, 220);
        ShowMatches();
    }

    /// <summary>Applied here so that clicking away keeps what's been ticked, as Finder's tags do.</summary>
    public override void ViewWillDisappear()
    {
        base.ViewWillDisappear();

        if (!_cancelled)
            commands.SetLabels(id, _opened, _names.Where(_ticked.Contains).ToList());
    }

    protected override void Cancelling() => _cancelled = true;

    protected override void ShowMatches()
    {
        var typed = Field.StringValue.Trim();

        _shown = typed.Length == 0
            ? _names
            : _names
                .Select(name => (Name: name, Score: Fuzzy.Score(name, typed)))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Name)
                .ToList();
        _fresh = typed.Length > 0 && !_names.Contains(typed, StringComparer.OrdinalIgnoreCase) ? typed : null;

        Table.ReloadData();

        // On the first row while narrowing, so Return takes the best match. On nothing otherwise:
        // with no word typed, Return closes the popover.
        if (typed.Length > 0 && RowCount > 0)
            Table.SelectRow(0, byExtendingSelection: false);
        else
            Table.DeselectAll(this);
    }

    protected override void Confirm()
    {
        if (Field.StringValue.Trim().Length == 0 && Table.SelectedRow < 0)
        {
            Close();
            return;
        }

        Toggle((int)Table.SelectedRow);
        Field.StringValue = string.Empty;
        ShowMatches();
    }

    private void Toggle(int row)
    {
        if (row < 0 || row >= RowCount)
            return;

        if (row == _shown.Count)
        {
            // Made when the popover closes, by which time it may have been unticked again.
            _names.Add(_fresh!);
            _ticked.Add(_fresh!);
        }
        else if (!_ticked.Remove(_shown[row]))
        {
            _ticked.Add(_shown[row]);
        }
    }

    private sealed class Source(LabelsController owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner.RowCount;
    }

    private sealed class Delegate(LabelsController owner) : NSTableViewDelegate
    {
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var at = (int)row;
            var cell = tableView.MakeView(CellId, owner) as Cell ?? new Cell(CellId);
            var fresh = at == owner._shown.Count;

            cell.Show(
                fresh ? string.Format(CultureInfo.CurrentCulture, Strings.LabelsNew, owner._fresh) : owner._shown[at],
                !fresh && owner._ticked.Contains(owner._shown[at]),
                () =>
                {
                    owner.Toggle(at);

                    // A label now, so the row offering to make it has done its job.
                    if (fresh)
                    {
                        owner.Field.StringValue = string.Empty;
                        owner.ShowMatches();
                    }
                });
            return cell;
        }
    }

    /// <summary>One label: a checkbox with its name for a title.</summary>
    private sealed class Cell : NSTableCellView
    {
        private readonly NSButton _box = new();
        private Action? _toggled;

        public Cell(string identifier)
        {
            Identifier = identifier;

            _box.SetButtonType(NSButtonType.Switch);
            _box.LineBreakMode = NSLineBreakMode.TruncatingTail;
            _box.TranslatesAutoresizingMaskIntoConstraints = false;
            _box.Activated += (_, _) => _toggled?.Invoke();
            AddSubview(_box);

            NSLayoutConstraint.ActivateConstraints(
            [
                _box.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 4),
                _box.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -4),
                _box.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            ]);
        }

        public void Show(string title, bool ticked, Action toggled)
        {
            _box.Title = title;
            _box.State = ticked ? NSCellStateValue.On : NSCellStateValue.Off;
            _toggled = toggled;
        }
    }
}
