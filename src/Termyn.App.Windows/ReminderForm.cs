using Termyn.Core.Capture;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The reminders on a task. Reminders are a paid feature, so on a plan without them the controls
/// that would write are shown disabled rather than hidden — the point is that the user can see what
/// they'd be getting, and nothing here ever offers a save the server would refuse.
/// </summary>
internal sealed class ReminderForm : Form
{
    private const string UpgradeMessage = "Todoist Pro required";

    /// <summary>The offsets offered, in minutes before the task is due.</summary>
    private static readonly (string Label, int Minutes)[] Offsets =
    [
        ("At the time it's due", 0),
        ("10 minutes before", 10),
        ("30 minutes before", 30),
        ("1 hour before", 60),
        ("1 day before", 1440),
    ];

    private readonly MainPresenter _presenter;
    private readonly string _itemId;
    private readonly ListBox _existing;
    private readonly ComboBox _offset;
    private readonly TextBox _absolute;
    private readonly Button _add;
    private readonly Button _addAbsolute;
    private readonly Button _remove;
    private readonly Label _message;
    private readonly ToolTip _tips = new();

    private bool _wrote;

    private ReminderForm(MainPresenter presenter, string itemId, string task)
    {
        _presenter = presenter;
        _itemId = itemId;

        Text = "Reminders";
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
        };

        _existing = new ListBox { Location = new Point(14, 38), Size = new Size(372, 150), IntegralHeight = false };
        _existing.SelectedIndexChanged += (_, _) => UpdateRemoveState();

        // The writing controls live in a panel so the "why" can be attached to something that still
        // receives the mouse: a disabled control gets no messages, so a tooltip on it never shows.
        var writes = new Panel { Location = new Point(14, 196), Size = new Size(372, 74) };

        var beforePrompt = new Label { Text = "Remind me", Location = new Point(0, 4), Size = new Size(76, 20) };
        _offset = new ComboBox
        {
            Location = new Point(78, 1),
            Size = new Size(180, 27),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var choice in Offsets)
            _offset.Items.Add(choice.Label);
        _offset.SelectedIndex = 0;

        _add = new Button { Text = "Add", Location = new Point(272, 0), Size = new Size(100, 29) };
        _add.Click += (_, _) => AddRelative();

        var atPrompt = new Label { Text = "or on", Location = new Point(0, 41), Size = new Size(76, 20) };
        _absolute = new TextBox { Location = new Point(78, 38), Size = new Size(180, 27), PlaceholderText = "2026-08-03 9am" };

        _addAbsolute = new Button { Text = "Add", Location = new Point(272, 37), Size = new Size(100, 29) };
        _addAbsolute.Click += (_, _) => AddAbsolute();

        writes.Controls.AddRange([beforePrompt, _offset, _add, atPrompt, _absolute, _addAbsolute]);

        _remove = new Button { Text = "Remove", Location = new Point(286, 278), Size = new Size(100, 29), Enabled = false };
        _remove.Click += (_, _) => Remove();

        _message = new Label
        {
            Location = new Point(14, 312),
            Size = new Size(372, 36),
            ForeColor = SystemColors.GrayText,
        };

        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Location = new Point(298, 358), Size = new Size(88, 30) };
        CancelButton = close;
        AcceptButton = close;

        Controls.AddRange([heading, _existing, writes, _remove, _message, close]);

        if (!presenter.RemindersAvailable)
        {
            // Only the controls that would write are gated. Removing a reminder is not a paid
            // operation, and a plan that lapsed leaves reminders behind that the user must be able
            // to clear.
            foreach (Control control in new Control[] { _offset, _add, _absolute, _addAbsolute })
                control.Enabled = false;

            // "Not allowed" and "not asked yet" both come through as unavailable, and telling a
            // paying user to buy what they already have is worse than saying we don't know yet.
            var reason = presenter.PlanName.Length == 0
                ? "Your plan hasn't synced yet."
                : UpgradeMessage;

            _message.Text = reason;
            _tips.SetToolTip(writes, reason);
        }

        Reload();
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

    /// <summary>What the line under the controls says.</summary>
    internal string Message => _message.Text;

    /// <summary>What's left in the box for a moment, which is cleared once one is added.</summary>
    internal string Typed => _absolute.Text;

    private void AddRelative()
    {
        if (_presenter.AddRelativeReminder(_itemId, Offsets[_offset.SelectedIndex].Minutes))
            Wrote();
        else
            _message.Text = Refusal();
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

        var verdict = DayBoxText.ForReminder(reading);
        if (!verdict.Accepted || reading.Day is not { } date)
        {
            _message.Text = verdict.Says;
            return;
        }

        if (_presenter.AddAbsoluteReminder(_itemId, date, reading.Time ?? DayBoxText.ReminderTime))
        {
            _absolute.Clear();
            Wrote();
        }
        else
        {
            _message.Text = Refusal();
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
            return _presenter.PlanName.Length == 0 ? "Your plan hasn't synced yet." : UpgradeMessage;

        // Asked of the account rather than the rows on screen: the outline only shows the current
        // view, so a task that is merely filtered out is still very much there.
        return _presenter.HasTask(_itemId)
            ? "This plan is at its limit for reminders."
            : "That task is no longer here.";
    }

    private void Wrote()
    {
        _wrote = true;
        _message.Text = string.Empty;
        Reload();
    }

    private void Reload()
    {
        _existing.Items.Clear();
        foreach (var reminder in _presenter.RemindersFor(_itemId))
            _existing.Items.Add(new ReminderRow(reminder));

        if (_existing.Items.Count == 0)
            _existing.Items.Add("No reminders on this task.");

        UpdateRemoveState();
    }

    private void UpdateRemoveState()
        => _remove.Enabled = _existing.SelectedItem is ReminderRow { CanRemove: true };
}
