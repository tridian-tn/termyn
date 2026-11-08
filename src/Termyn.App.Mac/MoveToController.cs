using ObjCRuntime;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The popover a task is moved to another project or section from: type a few letters of either,
/// or of the path to it, and press Return.
/// </summary>
/// <remarks>
/// The ranking and where the list rests are <see cref="MoveDestinations"/>' own, so it finds the
/// same places in the same order as the Windows picker. Browsing, the list is the tree and the
/// indent says what's inside what; searching, it's a ranking whose rows no longer sit under their
/// parents, so each says its whole path. Where the task already sits is ticked, and can't be chosen.
/// </remarks>
internal sealed class MoveToController(MainPresenter presenter, TaskCommands commands, string id, Action close) : SearchPopoverController(close)
{
    private const string CellId = "Destination";

    private IReadOnlyList<MoveDestination> _all = [];
    private IReadOnlyList<MoveDestination> _shown = [];
    private NSButton? _move;

    /// <summary>Whether the list is a ranking of what's been typed rather than the whole tree.</summary>
    private bool _searching;

    protected override int RowCount => _shown.Count;

    public override void LoadView()
    {
        _all = presenter.DestinationsFor(id);

        Table.Target = this;
        Table.DoubleAction = new Selector("moveClicked:");

        _move = NSButton.CreateButton(Strings.MoveButton, Confirm);
        _move.KeyEquivalent = "\r";

        View = Lay(commands.Row(id)?.Content ?? string.Empty, Strings.MovePlaceholder, new Source(this), new Delegate(this), 360, 260, _move);
        ShowMatches();
    }

    private MoveDestination? Selected
        => Table.SelectedRow >= 0 && Table.SelectedRow < _shown.Count ? _shown[(int)Table.SelectedRow] : null;

    protected override void ShowMatches()
    {
        _searching = Field.StringValue.Trim().Length > 0;
        _shown = MoveDestinations.Rank(_all, Field.StringValue);
        Table.ReloadData();

        // Set whichever way it goes, so where Return sends the task never rests on what a reload
        // happens to leave selected.
        var rest = MoveDestinations.Preselect(_shown, _searching);
        if (rest >= 0)
        {
            Table.SelectRow(rest, byExtendingSelection: false);
            Table.ScrollRowToVisible(rest);
        }
        else
        {
            Table.DeselectAll(this);
        }

        ShowButton();
    }

    private void ShowButton() => _move!.Enabled = Selected is { Here: false };

    [Export("moveClicked:")]
    private void MoveClicked(NSObject sender) => Confirm();

    protected override void Confirm()
    {
        if (Selected is not { Here: false } destination)
            return;

        Close();
        commands.MoveTo(id, destination);
    }

    private sealed class Source(MoveToController owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._shown.Count;
    }

    private sealed class Delegate(MoveToController owner) : NSTableViewDelegate
    {
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var cell = tableView.MakeView(CellId, owner) as Cell ?? new Cell(CellId);
            cell.Show(owner._shown[(int)row], owner._searching);
            return cell;
        }

        public override void SelectionDidChange(NSNotification notification) => owner.ShowButton();
    }

    /// <summary>
    /// One place: a tick if the task's already there, its name, and whether it's a project or a
    /// section, since a child project and a section look alike once they're indented under the
    /// same parent.
    /// </summary>
    private sealed class Cell : NSTableCellView
    {
        private const int IndentPerLevel = 14;

        private readonly NSImageView _tick = new();
        private readonly NSTextField _name = NSTextField.CreateLabel(string.Empty);
        private readonly NSTextField _kind = NSTextField.CreateLabel(string.Empty);
        private readonly NSLayoutConstraint _indent;

        public Cell(string identifier)
        {
            Identifier = identifier;

            _tick.Image = NSImage.GetSystemSymbol("checkmark", null);
            _name.LineBreakMode = NSLineBreakMode.TruncatingTail;
            _name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            _kind.TextColor = NSColor.SecondaryLabel;
            _kind.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);

            foreach (var view in new NSView[] { _tick, _name, _kind })
            {
                view.TranslatesAutoresizingMaskIntoConstraints = false;
                AddSubview(view);
            }

            TextField = _name;
            _indent = _name.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 22);

            NSLayoutConstraint.ActivateConstraints(
            [
                _indent,
                _tick.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 4),
                _tick.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
                _name.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
                _kind.LeadingAnchor.ConstraintGreaterThanOrEqualTo(_name.TrailingAnchor, 6),
                _kind.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -6),
                _kind.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            ]);
        }

        public void Show(MoveDestination destination, bool searching)
        {
            _tick.Hidden = !destination.Here;
            _name.StringValue = searching ? destination.Path : destination.Name;
            _kind.StringValue = destination.Kind == SidebarKind.Section ? Strings.MoveKindSection : Strings.MoveKindProject;
            _indent.Constant = 22 + (searching ? 0 : destination.Depth * IndentPerLevel);
        }
    }
}
