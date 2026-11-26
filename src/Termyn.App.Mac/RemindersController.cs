using System.Globalization;
using Termyn.Core.Capture;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The popover a task's reminders are listed in: what it has, each with a way to remove it, and the
/// two ways of adding another.
/// </summary>
/// <remarks>
/// The Windows reminders dialog in a popover. Reminders are a paid feature, so on a plan without
/// them the controls that would write are shown greyed rather than hidden. Removing isn't gated: a
/// plan that lapsed leaves reminders behind that have to be clearable. An offset is greyed as well
/// on a task with no due time, which Todoist would refuse, since it has nothing to count back from.
///
/// Each change is written as it's made, so there's nothing for Esc to leave undone. The moment is
/// read the way the date boxes read a day, and the line under it says what it's been read as, as
/// the due popover's does. Return adds what's typed, or closes the popover when nothing is.
///
/// The keyboard stays in the field, as it does in the labels popover: ↑ and ↓ move through the list
/// from there, and ⌫ with nothing typed removes the reminder they're on.
///
/// It follows the account while it's up, since a sync can change the plan, the task's due time or
/// the reminders themselves.
/// </remarks>
internal sealed class RemindersController(MainPresenter presenter, TaskCommands commands, string id, Action close) : NSViewController
{
    private const string CellId = "Reminder";
    private const int Margin = 14;
    private const int Width = 360;

    /// <summary>
    /// The offsets offered, in minutes before the task is due.
    /// </summary>
    /// <remarks>
    /// Built when asked for rather than once, so the labels are read in the language in force when
    /// the popover opens.
    /// </remarks>
    private static (string Label, int Minutes)[] Offsets =>
    [
        (Strings.RemindersAtTime, 0),
        (string.Format(CultureInfo.CurrentCulture, Strings.RemindersMinutesBefore, 10), 10),
        (string.Format(CultureInfo.CurrentCulture, Strings.RemindersMinutesBefore, 30), 30),
        (Strings.RemindersHourBefore, 60),
        (Strings.RemindersDayBefore, 1440),
    ];

    private readonly NSTableView _table = new();
    private readonly NSPopUpButton _offset = new();
    private readonly NSTextField _moment = new();
    private readonly NSTextField _message = PopoverParts.SmallLine(Width - (2 * Margin));
    private readonly NSButton _addOffset = new();
    private readonly NSButton _addMoment = new();

    private IReadOnlyList<ReminderRow> _rows = [];

    /// <summary>Whether it's listening for what a sync brings, so it only stops once.</summary>
    private bool _following;

    public override void LoadView()
    {
        var name = PopoverParts.Heading(commands.Row(id)?.Content ?? string.Empty);

        var scroll = PopoverParts.List(_table, new Source(this), new Delegate(this));
        _table.RowHeight = 24;

        _offset.AddItems(Offsets.Select(o => o.Label).ToArray());

        _moment.PlaceholderString = DayBoxText.ReminderHint(presenter.DatesInEnglish);
        _moment.Delegate = new FieldKeys(ShowState, Confirm, close) { Step = Step, Backspace = RemoveSelected };

        Button(_addOffset, AddOffset);
        Button(_addMoment, AddMoment);

        var form = NSGridView.Create(
        [
            [PopoverParts.Prompt(Strings.RemindersRemindMe), _offset, _addOffset],
            [PopoverParts.Prompt(Strings.RemindersOrOn), _moment, _addMoment],
        ]);
        form.ColumnSpacing = 8;
        form.RowSpacing = 8;
        form.RowAlignment = NSGridRowAlignment.FirstBaseline;
        form.GetColumn(0).X = NSGridCellPlacement.Trailing;

        var stack = NSStackView.FromViews([name, scroll, form, _message]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 8;
        stack.EdgeInsets = new NSEdgeInsets(Margin, Margin, Margin, Margin);

        NSLayoutConstraint.ActivateConstraints(
        [
            stack.WidthAnchor.ConstraintEqualTo(Width),
            name.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -2 * Margin),
            scroll.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -2 * Margin),
            scroll.HeightAnchor.ConstraintEqualTo(120),
            _message.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -2 * Margin),
            _moment.WidthAnchor.ConstraintEqualTo(_offset.WidthAnchor),
        ]);

        View = stack;

        Reload();
        ShowState();
    }

    public override void ViewWillAppear()
    {
        base.ViewWillAppear();

        if (!_following)
            presenter.RowsChanged += OnChanged;

        _following = true;
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();

        if (_moment.Enabled)
            View.Window?.MakeFirstResponder(_moment);
    }

    public override void ViewDidDisappear()
    {
        base.ViewDidDisappear();

        if (_following)
            presenter.RowsChanged -= OnChanged;

        _following = false;
    }

    private static void Button(NSButton button, Action pressed)
    {
        button.Title = Strings.RemindersAdd;
        button.BezelStyle = NSBezelStyle.Push;
        button.Activated += (_, _) => pressed();
    }

    /// <summary>A sync has come in, on whichever thread it finished on.</summary>
    private void OnChanged() => BeginInvokeOnMainThread(() =>
    {
        if (_following)
        {
            Reload();
            ShowState();
        }
    });

    private void AddOffset()
    {
        var minutes = Offsets[(int)_offset.IndexOfSelectedItem].Minutes;
        Add(current => presenter.AddRelativeReminder(current, minutes));
    }

    /// <summary>
    /// Adds a reminder at the moment typed, and empties the field for the next one. A day with no
    /// time on it is at nine, as on Windows.
    /// </summary>
    private void AddMoment()
    {
        var reading = presenter.ReadDay(_moment.StringValue);
        if (reading.Kind is DayReadingKind.Blank)
            return;

        var verdict = DayBoxText.ForReminder(reading, presenter.DatesInEnglish);
        if (!verdict.Accepted || reading.Day is not { } date)
        {
            Say(verdict.Says, refused: true);
            return;
        }

        if (Add(current => presenter.AddAbsoluteReminder(current, date, reading.Time ?? DayBoxText.ReminderTime)))
        {
            _moment.StringValue = string.Empty;
            ShowState();
        }
    }

    /// <summary>Makes an add, and says why when it's turned down.</summary>
    /// <param name="add">The add, given what the task is called now</param>
    /// <returns>True when it was added</returns>
    private bool Add(Func<string, bool> add)
    {
        var added = false;

        // One that threw has already said what went wrong.
        if (commands.Guarded(() => added = add(commands.CurrentIdOf(id))) && !added)
        {
            Say(Refusal(), refused: true);
        }
        else if (added)
        {
            Reload();
            ShowState();
        }

        return added;
    }

    private void Remove(ReminderRow row)
    {
        commands.Guarded(() => presenter.DeleteReminder(row.Reminder.Id));
        Reload();

        // It may well have made room, so a refusal for being at the limit no longer stands.
        ShowState();
    }

    /// <summary>⌫ in the field: removes the row the arrows are on, once there's nothing left to delete.</summary>
    /// <returns>True when it dealt with the key</returns>
    private bool RemoveSelected()
    {
        if (_moment.StringValue.Length > 0 || _table.SelectedRow < 0 || _table.SelectedRow >= _rows.Count)
            return false;

        if (_rows[(int)_table.SelectedRow] is { CanRemove: true } row)
            Remove(row);

        return true;
    }

    /// <summary>↑ or ↓ in the field: moves through the reminders, if there are any.</summary>
    private void Step(int by)
    {
        if (_rows.Count == 0)
            return;

        var row = Math.Clamp((int)_table.SelectedRow + by, 0, _rows.Count - 1);
        _table.SelectRow(row, byExtendingSelection: false);
        _table.ScrollRowToVisible(row);
    }

    /// <summary>
    /// The reminders the task has now. By what it's called now, too: one added a moment ago is
    /// renamed when the sync hears what the server calls it, and the old name would find nothing.
    /// </summary>
    /// <remarks>Stays on the same row, or the last one if that's gone, so ⌫ can be pressed again.</remarks>
    private void Reload()
    {
        var selected = (int)_table.SelectedRow;

        _rows = presenter.ReminderRowsFor(commands.CurrentIdOf(id));
        _table.ReloadData();

        if (selected >= 0 && _rows.Count > 0)
            _table.SelectRow(Math.Min(selected, _rows.Count - 1), byExtendingSelection: false);
        else
            _table.DeselectAll(this);
    }

    /// <summary>
    /// Why an add was turned down. A background sync runs while this is open, so the task itself may
    /// have gone, and blaming the plan for that would send the user to check the wrong thing.
    /// </summary>
    private string Refusal()
    {
        if (!presenter.RemindersAvailable)
            return PlanReason();

        return presenter.HasTask(id) ? Strings.RemindersAtLimit : Strings.RemindersTaskGone;
    }

    /// <summary>
    /// "Not allowed" and "not asked yet" both come through as unavailable, and telling a paying user
    /// to buy what they already have is worse than saying it isn't known yet.
    /// </summary>
    private string PlanReason() => presenter.PlanName.Length == 0 ? Strings.RemindersPlanNotSynced : Strings.RemindersUpgrade;

    private void Say(string words, bool refused)
    {
        _message.StringValue = words;
        _message.TextColor = refused ? NSColor.SystemRed : NSColor.SecondaryLabel;
    }

    /// <summary>
    /// Puts the controls and the line under them right for how things stand: greyed for what can't
    /// be done, and saying what's typed will be read as, or else why something's greyed.
    /// </summary>
    private void ShowState()
    {
        var available = presenter.RemindersAvailable;
        var offsets = available && presenter.TakesRelativeReminder(id);
        var typed = _moment.StringValue.Trim().Length > 0;

        _offset.Enabled = offsets;
        _addOffset.Enabled = offsets;
        _moment.Enabled = available;
        _addMoment.Enabled = available && typed;

        if (!available)
        {
            Say(PlanReason(), refused: false);
        }
        else if (typed)
        {
            var verdict = DayBoxText.ForReminder(presenter.ReadDay(_moment.StringValue), presenter.DatesInEnglish);
            Say(verdict.Says, refused: !verdict.Accepted);
        }
        else
        {
            Say(offsets ? string.Empty : Strings.RemindersNeedDueTime, refused: false);
        }
    }

    /// <summary>Return in the field: adds what's typed, or closes when there's nothing to lose.</summary>
    private void Confirm()
    {
        if (_moment.StringValue.Trim().Length == 0)
            close();
        else
            AddMoment();
    }

    /// <summary>A row for each reminder, or a row to say there are none.</summary>
    private sealed class Source(RemindersController owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => Math.Max(owner._rows.Count, 1);
    }

    private sealed class Delegate(RemindersController owner) : NSTableViewDelegate
    {
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row)
        {
            var cell = tableView.MakeView(CellId, owner) as Cell ?? new Cell(CellId);

            if (owner._rows.Count == 0)
            {
                cell.Show(Strings.RemindersNone, NSColor.SecondaryLabel, null);
            }
            else
            {
                // A kind Termyn couldn't put back is a one-way door, so it isn't offered.
                var reminder = owner._rows[(int)row];
                cell.Show(reminder.ToString(), NSColor.Label, reminder.CanRemove ? () => owner.Remove(reminder) : null);
            }

            return cell;
        }

        /// <summary>The row saying there are none can't be selected, having nothing to act on.</summary>
        public override bool ShouldSelectRow(NSTableView tableView, nint row) => owner._rows.Count > 0;
    }

    /// <summary>One reminder: what it says, and a button to remove it where that can be done.</summary>
    private sealed class Cell : NSTableCellView
    {
        private readonly NSTextField _words = NSTextField.CreateLabel(string.Empty);
        private readonly NSButton _remove = new();
        private Action? _removed;

        public Cell(string identifier)
        {
            Identifier = identifier;

            _words.LineBreakMode = NSLineBreakMode.TruncatingTail;
            _words.TranslatesAutoresizingMaskIntoConstraints = false;
            _words.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            AddSubview(_words);

            // The symbol's description is what VoiceOver reads the button as.
            _remove.Image = NSImage.GetSystemSymbol("minus.circle", Strings.RemindersRemove);
            _remove.ImagePosition = NSCellImagePosition.ImageOnly;
            _remove.Bordered = false;
            _remove.ToolTip = Strings.RemindersRemove;
            _remove.TranslatesAutoresizingMaskIntoConstraints = false;
            _remove.Activated += (_, _) => _removed?.Invoke();
            AddSubview(_remove);

            NSLayoutConstraint.ActivateConstraints(
            [
                _words.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 6),
                _words.TrailingAnchor.ConstraintLessThanOrEqualTo(_remove.LeadingAnchor, -6),
                _words.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
                _remove.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -6),
                _remove.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            ]);
        }

        public void Show(string words, NSColor colour, Action? removed)
        {
            _words.StringValue = words;
            _words.TextColor = colour;
            _words.ToolTip = words;
            _remove.Hidden = removed is null;
            _removed = removed;
        }
    }
}
