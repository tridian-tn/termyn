using Termyn.Core.Model;
using Termyn.Presentation;

using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// The reminders on a task. Reminders are a paid feature, so on a plan without them the controls
/// are shown disabled rather than hidden — the point is that the user can see what they'd be
/// getting, and nothing here ever offers a save the server would refuse.
/// </summary>
internal sealed class ReminderForm : Form
{
    private const string UpgradeMessage = "Todoist Pro required";

    private readonly MainPresenter _presenter;
    private readonly string _itemId;
    private readonly ListBox _existing;
    private readonly ComboBox _offset;
    private readonly Button _add;
    private readonly Button _remove;
    private readonly ToolTip _tips = new();

    private ReminderForm(MainPresenter presenter, string itemId, string task)
    {
        _presenter = presenter;
        _itemId = itemId;

        Text = "Reminders";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(400, 330);

        var heading = new Label
        {
            Text = task,
            Location = new Point(14, 12),
            Size = new Size(372, 20),
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
        };

        _existing = new ListBox { Location = new Point(14, 38), Size = new Size(372, 160), IntegralHeight = false };

        var prompt = new Label { Text = "Remind me", Location = new Point(14, 212), Size = new Size(76, 20) };

        _offset = new ComboBox
        {
            Location = new Point(92, 209),
            Size = new Size(180, 27),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var choice in Offsets)
            _offset.Items.Add(choice.Label);
        _offset.SelectedIndex = 0;

        _add = new Button { Text = "Add", Location = new Point(286, 208), Size = new Size(100, 29) };
        _add.Click += (_, _) => Add();

        _remove = new Button { Text = "Remove", Location = new Point(286, 243), Size = new Size(100, 29) };
        _remove.Click += (_, _) => Remove();

        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Location = new Point(298, 288), Size = new Size(88, 30) };
        CancelButton = close;
        AcceptButton = close;

        Controls.AddRange([heading, _existing, prompt, _offset, _add, _remove, close]);

        if (!presenter.RemindersAvailable)
        {
            // Disabled, with the reason attached to each control the user would reach for. Saying
            // nothing would read as the app being broken.
            _offset.Enabled = false;
            _add.Enabled = false;
            _remove.Enabled = false;

            heading.Text = $"{task}\r\n{UpgradeMessage}";
            heading.Size = new Size(372, 36);

            foreach (Control control in new Control[] { _offset, _add, _remove })
                _tips.SetToolTip(control, UpgradeMessage);
        }

        Refresh(presenter);
        FormClosed += (_, _) => _tips.Dispose();
    }

    /// <summary>The offsets offered, in minutes before the task is due.</summary>
    private static (string Label, int Minutes)[] Offsets =>
    [
        ("At the time it's due", 0),
        ("10 minutes before", 10),
        ("30 minutes before", 30),
        ("1 hour before", 60),
        ("1 day before", 1440),
    ];

    public static void Show(IWin32Window owner, MainPresenter presenter, string itemId, string task)
    {
        using var dialog = new ReminderForm(presenter, itemId, task);
        dialog.ShowDialog(owner);
    }

    private void Add()
    {
        var minutes = Offsets[_offset.SelectedIndex].Minutes;
        if (_presenter.AddRelativeReminder(_itemId, minutes))
            Refresh(_presenter);
    }

    private void Remove()
    {
        if (_existing.SelectedItem is not ReminderRow row)
            return;

        // A location reminder was set somewhere with a map. Removing it from here would be a
        // one-way door: nothing in Termyn could put it back.
        if (row.Reminder.Kind == ReminderKind.Location)
            return;

        _presenter.DeleteReminder(row.Reminder.Id);
        Refresh(_presenter);
    }

    private void Refresh(MainPresenter presenter)
    {
        _existing.Items.Clear();
        foreach (var reminder in presenter.RemindersFor(_itemId))
            _existing.Items.Add(new ReminderRow(reminder));

        if (_existing.Items.Count == 0)
            _existing.Items.Add("No reminders on this task.");
    }

    /// <summary>Wraps a reminder so the list can show it in words.</summary>
    private sealed record ReminderRow(Reminder Reminder)
    {
        public override string ToString() => Reminder.Kind switch
        {
            ReminderKind.Absolute => $"At {Reminder.DueDate}",
            ReminderKind.Location => $"At {Reminder.LocationName ?? "a place"} (set in Todoist)",
            _ => Reminder.MinuteOffset == 0
                ? "When it's due"
                : $"{Describe(Reminder.MinuteOffset)} before it's due",
        };

        private static string Describe(int minutes) => minutes switch
        {
            < 60 => $"{minutes} minutes",
            < 1440 when minutes % 60 == 0 => $"{minutes / 60} hour{(minutes == 60 ? "" : "s")}",
            _ when minutes % 1440 == 0 => $"{minutes / 1440} day{(minutes == 1440 ? "" : "s")}",
            _ => $"{minutes} minutes",
        };
    }
}
