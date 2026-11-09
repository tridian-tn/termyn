using System.Globalization;
using Termyn.Core.Capture;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The reminders on a task. Reminders are a paid feature, so on a plan without them the controls
/// that would write are shown disabled rather than hidden — the point is that the user can see what
/// they'd be getting, and nothing here ever offers a save the server would refuse. The same goes for
/// the offsets on a task with no due time, which have nothing to count back from.
/// </summary>
internal sealed class ReminderForm : Form
{
    private static string UpgradeMessage => Strings.RemindersUpgrade;

    /// <summary>
    /// The offsets offered, in minutes before the task is due.
    /// </summary>
    /// <remarks>
    /// Built when asked for rather than once, so the labels are read in the language in force
    /// when the dialog opens rather than whichever one the type happened to load under.
    /// </remarks>
    private static (string Label, int Minutes)[] Offsets =>
    [
        (Strings.RemindersAtTime, 0),
        (Before(10), 10),
        (Before(30), 30),
        (Strings.RemindersHourBefore, 60),
        (Strings.RemindersDayBefore, 1440),
    ];

    /// <summary>An offset of so many minutes, as the list says it.</summary>
    private static string Before(int minutes)
        => string.Format(CultureInfo.CurrentCulture, Strings.RemindersMinutesBefore, minutes);

    private readonly MainPresenter _presenter;
    private readonly string _itemId;
    private readonly ListBox _existing;
    private readonly Panel _writes;
    private readonly ComboBox _offset;
    private readonly TextBox _absolute;
    private readonly Button _add;
    private readonly Button _addAbsolute;
    private readonly Button _remove;
    private readonly Label _message;
    private readonly ToolTip _tips = new();

    private bool _wrote;

    /// <summary>
    /// Something the line is saying about the last thing tried, which a sync coming in shouldn't
    /// take away from under someone reading it. Null when the line is free to say why something's greyed.
    /// </summary>
    private string? _notice;

    private ReminderForm(MainPresenter presenter, string itemId, string task)
    {
        _presenter = presenter;
        _itemId = itemId;

        Text = Strings.RemindersTitle;
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(400, 400);

        var heading = new Label
        {
            Text = task,
            Location = new Point(14, 12),
            Size = new Size(372, 20),
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,

            // The task's own name, in which an ampersand is a character rather than an access key.
            UseMnemonic = false,
        };

        _existing = new ListBox { Location = new Point(14, 38), Size = new Size(372, 150), IntegralHeight = false };
        _existing.SelectedIndexChanged += (_, _) => UpdateRemoveState();

        // The writing controls live in a panel so the "why" can be attached to something that still
        // receives the mouse: a disabled control gets no messages, so a tooltip on it never shows.
        _writes = new Panel { Location = new Point(14, 196), Size = new Size(372, 74) };

        var beforePrompt = new Label { Text = Strings.RemindersRemindMe, Location = new Point(0, 4), Size = new Size(76, 20) };
        _offset = new ComboBox
        {
            Location = new Point(78, 1),
            Size = new Size(180, 27),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var choice in Offsets)
            _offset.Items.Add(choice.Label);
        _offset.SelectedIndex = 0;

        _add = new Button { Text = Strings.RemindersAdd, Location = new Point(272, 0), Size = new Size(100, 29) };
        _add.Click += (_, _) => AddRelative();

        var atPrompt = new Label { Text = Strings.RemindersOrOn, Location = new Point(0, 41), Size = new Size(76, 20) };
        _absolute = new TextBox { Location = new Point(78, 38), Size = new Size(180, 27), PlaceholderText = DayBoxText.ReminderHint(presenter.DatesInEnglish) };

        _addAbsolute = new Button { Text = Strings.RemindersAdd, Location = new Point(272, 37), Size = new Size(100, 29) };
        _addAbsolute.Click += (_, _) => AddAbsolute();

        _writes.Controls.AddRange([beforePrompt, _offset, _add, atPrompt, _absolute, _addAbsolute]);

        _remove = new Button { Text = Strings.RemindersRemove, Location = new Point(286, 278), Size = new Size(100, 29), Enabled = false };
        _remove.Click += (_, _) => Remove();

        _message = new Label
        {
            Location = new Point(14, 312),
            Size = new Size(372, 36),
            ForeColor = SystemColors.GrayText,
        };

        var close = new Button { Text = Strings.ButtonClose, DialogResult = DialogResult.OK, Location = new Point(298, 358), Size = new Size(88, 30) };
        CancelButton = close;
        AcceptButton = close;

        Controls.AddRange([heading, _existing, _writes, _remove, _message, close]);

        Reload();
        ShowWhatsOffered();
        FormClosed += (_, _) => _tips.Dispose();
    }

    /// <summary>Shows the dialog.</summary>
    /// <returns>True when a reminder was added or removed, so the caller can schedule a sync.</returns>
    public static bool Show(IWin32Window owner, MainPresenter presenter, string itemId, string task)
    {
        using var dialog = new ReminderForm(presenter, itemId, task);
        dialog.ShowDialog(owner);
        return dialog._wrote;
    }

    /// <summary>The dialog as it will be shown, built and not shown, for a test to look at.</summary>
    /// <param name="presenter">The presenter the reminders are read from and written through</param>
    /// <param name="itemId">The task the reminders are on</param>
    /// <param name="task">What the task is called</param>
    /// <returns>The dialog, which the caller disposes</returns>
    internal static ReminderForm For(MainPresenter presenter, string itemId, string task) => new(presenter, itemId, task);

    /// <summary>
    /// Types a moment and presses its Add, for a test with no dialog to click.
    /// </summary>
    /// <remarks>
    /// Raises the button's own Click rather than calling what it's wired to, so the wiring is part
    /// of what's covered.
    /// </remarks>
    /// <param name="typed">The moment, as it would be typed</param>
    internal void AddAt(string typed)
    {
        _absolute.Text = typed;
        InvokeOnClick(_addAbsolute, EventArgs.Empty);
    }

    /// <summary>Types a moment and presses Enter in its box, for a test with no keyboard.</summary>
    /// <param name="typed">The moment, as it would be typed</param>
    /// <returns>Whether the dialog took the key</returns>
    internal bool EnterAt(string typed)
    {
        _absolute.Focus();
        _absolute.Text = typed;
        return ProcessDialogKey(Keys.Enter);
    }

    /// <summary>
    /// Enter in the box for a moment adds what's typed there.
    /// </summary>
    /// <remarks>
    /// Close is the dialog's accept button as well as its cancel, so Enter anywhere else closes it —
    /// and in the box, it used to close it with the reminder just typed thrown away. An empty box
    /// still closes, since there's nothing in it to lose.
    /// </remarks>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter && _absolute.Focused && _addAbsolute.Enabled && !string.IsNullOrWhiteSpace(_absolute.Text))
        {
            AddAbsolute();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    /// <summary>What the line under the controls says.</summary>
    internal string Message => _message.Text;

    /// <summary>What's left in the box for a moment, which is cleared once one is added.</summary>
    internal string Typed => _absolute.Text;

    /// <summary>What the box for a moment suggests typing while it's empty.</summary>
    internal string Hint => _absolute.PlaceholderText;

    /// <summary>What the list of reminders says, row by row.</summary>
    internal IReadOnlyList<string> Listed => [.. _existing.Items.Cast<object>().Select(row => row.ToString() ?? string.Empty)];

    /// <summary>What the mouse is told over the writing controls.</summary>
    internal string Tip => _tips.GetToolTip(_writes) ?? string.Empty;

    /// <summary>Whether the list of offsets can be used.</summary>
    internal bool OffsetChoiceEnabled => _offset.Enabled;

    /// <summary>Whether the offset's Add can be pressed.</summary>
    internal bool OffsetAddEnabled => _add.Enabled;

    /// <summary>Whether the box for a moment can be typed in.</summary>
    internal bool MomentBoxEnabled => _absolute.Enabled;

    /// <summary>Whether the moment's Add can be pressed.</summary>
    internal bool MomentAddEnabled => _addAbsolute.Enabled;

    /// <summary>Whether Remove can be pressed.</summary>
    internal bool RemoveEnabled => _remove.Enabled;

    /// <summary>Chooses an offset and presses its Add, for a test with no dialog to click.</summary>
    /// <remarks>
    /// A click reaches a greyed button from here as it couldn't from the mouse, which is what lets a
    /// test press one that was lit when it was last looked at.
    /// </remarks>
    /// <param name="choice">Where the offset is in the list</param>
    internal void AddOffset(int choice)
    {
        _offset.SelectedIndex = choice;
        InvokeOnClick(_add, EventArgs.Empty);
    }

    /// <summary>Selects a reminder in the list, for a test with no mouse.</summary>
    /// <param name="row">Where the reminder is in the list</param>
    internal void SelectReminder(int row) => _existing.SelectedIndex = row;

    /// <summary>Presses Remove, for a test with no dialog to click.</summary>
    internal void PressRemove() => InvokeOnClick(_remove, EventArgs.Empty);

    /// <summary>
    /// What the task is called now. One added a moment ago is renamed when a sync hears what the
    /// server calls it, and the old name would find no reminders on it.
    /// </summary>
    private string CurrentId => _presenter.CurrentIdOf(_itemId);

    /// <summary>Follows a sync for as long as there's a window to show it in.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _presenter.RowsChanged += OnRowsChanged;
    }

    /// <inheritdoc/>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        _presenter.RowsChanged -= OnRowsChanged;
        base.OnHandleDestroyed(e);
    }

    /// <summary>Something has changed, on whichever thread it finished on.</summary>
    private void OnRowsChanged()
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        if (!InvokeRequired)
        {
            CatchUp();
            return;
        }

        try
        {
            BeginInvoke(CatchUp);
        }
        catch (InvalidOperationException)
        {
            // Closed between asking and posting. This is the sync's thread, and a dialog that has
            // gone is no reason to fail the sync.
        }
    }

    /// <summary>Puts the list and the controls right for how things stand now.</summary>
    private void CatchUp()
    {
        Reload();
        ShowWhatsOffered();
    }

    private void AddRelative()
    {
        // Asked again at the press, for a sync that has taken away the due time the button was lit
        // for and whose news hasn't reached this thread yet. Todoist is expected to refuse the add,
        // but only later on.
        if (!_presenter.TakesRelativeReminder(CurrentId))
        {
            _notice = null;
            ShowWhatsOffered();
            return;
        }

        if (_presenter.AddRelativeReminder(CurrentId, Offsets[_offset.SelectedIndex].Minutes))
            Wrote();
        else
            Say(Refusal());
    }

    /// <summary>
    /// Adds a reminder at the moment typed, read the way the date boxes read a day.
    /// </summary>
    /// <remarks>
    /// Not the capture parser, which keeps whatever it doesn't read as a task's title and so never
    /// refuses anything: "tomorrow please" was tomorrow at nine with "please" dropped.
    /// </remarks>
    private void AddAbsolute()
    {
        var reading = _presenter.ReadDay(_absolute.Text);
        if (reading.Kind is DayReadingKind.Blank)
            return;

        var verdict = DayBoxText.ForReminder(reading, _presenter.DatesInEnglish);
        if (!verdict.Accepted || reading.Day is not { } date)
        {
            Say(verdict.Says);
            return;
        }

        if (_presenter.AddAbsoluteReminder(CurrentId, date, reading.Time ?? DayBoxText.ReminderTime))
        {
            _absolute.Clear();
            Wrote();
        }
        else
        {
            Say(Refusal());
        }
    }

    private void Remove()
    {
        if (_existing.SelectedItem is not ReminderRow { CanRemove: true } row)
            return;

        _presenter.DeleteReminder(row.Reminder.Id);
        Wrote();
    }

    /// <summary>
    /// Why an add was turned down. A background sync runs while this dialog is open, so the task
    /// itself may have gone — blaming the plan for that would send the user to check the wrong thing.
    /// </summary>
    private string Refusal()
    {
        if (!_presenter.RemindersAvailable)
            return PlanReason();

        // Asked of the account rather than the rows on screen: the outline only shows the current
        // view, so a task that is merely filtered out is still very much there.
        return _presenter.HasTask(CurrentId)
            ? Strings.RemindersAtLimit
            : Strings.RemindersTaskGone;
    }

    /// <summary>
    /// "Not allowed" and "not asked yet" both come through as unavailable, and telling a paying user
    /// to buy what they already have is worse than saying we don't know yet.
    /// </summary>
    private string PlanReason()
        => _presenter.PlanName.Length == 0 ? Strings.RemindersPlanNotSynced : UpgradeMessage;

    /// <summary>
    /// Greys what can't be done, and says why.
    /// </summary>
    /// <remarks>
    /// Only the controls that would write are gated. Removing a reminder is not a paid operation, and
    /// a plan that lapsed leaves reminders behind that the user must be able to clear. A task with no
    /// due time still takes a moment of its own, which doesn't count back from anything: Todoist says
    /// "the item should have a due date with time set in order to add a relative reminder".
    /// </remarks>
    private void ShowWhatsOffered()
    {
        var id = CurrentId;
        var available = _presenter.RemindersAvailable;
        var offsets = available && _presenter.TakesRelativeReminder(id);

        _offset.Enabled = offsets;
        _add.Enabled = offsets;
        _absolute.Enabled = available;
        _addAbsolute.Enabled = available;

        var reason = !available ? PlanReason()
            : offsets ? string.Empty
            : _presenter.HasTask(id) ? Strings.RemindersNeedDueTime
            : Strings.RemindersTaskGone;

        // With everything greyed, the plan is the only thing left worth saying.
        _message.Text = available && _notice is { } notice ? notice : reason;
        _tips.SetToolTip(_writes, reason);
    }

    /// <summary>Says something about the last thing tried, in place of why anything's greyed.</summary>
    /// <remarks>Whatever turned it down may also mean the controls no longer stand.</remarks>
    /// <param name="notice">What to say</param>
    private void Say(string notice)
    {
        _notice = notice;
        ShowWhatsOffered();
    }

    private void Wrote()
    {
        _wrote = true;
        _notice = null;
        CatchUp();
    }

    /// <summary>
    /// The reminders the task has now, keeping the selection and the scroll: a sync can arrive just
    /// as someone reaches for Remove.
    /// </summary>
    private void Reload()
    {
        var selected = _existing.SelectedItem is ReminderRow row ? _presenter.CurrentIdOf(row.Reminder.Id) : null;
        var top = _existing.TopIndex;

        _existing.BeginUpdate();
        _existing.Items.Clear();
        foreach (var reminder in _presenter.ReminderRowsFor(CurrentId))
            _existing.Items.Add(reminder);

        if (_existing.Items.Count == 0)
            _existing.Items.Add(Strings.RemindersNone);

        for (var i = 0; i < _existing.Items.Count; i++)
        {
            if (_existing.Items[i] is ReminderRow { Reminder.Id: var id } && id == selected)
                _existing.SelectedIndex = i;
        }

        _existing.TopIndex = Math.Min(top, _existing.Items.Count - 1);
        _existing.EndUpdate();

        UpdateRemoveState();
    }

    private void UpdateRemoveState()
        => _remove.Enabled = _existing.SelectedItem is ReminderRow { CanRemove: true };
}
