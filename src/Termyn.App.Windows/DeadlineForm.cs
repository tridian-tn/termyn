using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// The day a task has to be finished by.
/// </summary>
/// <remarks>
/// A calendar where the due date has a text box. A due date can be anything Todoist's own parser
/// will read — "every Monday" among them — and whatever this app can't read is sent as the words
/// themselves for the server to settle. A deadline has no such field: the API takes a date and
/// nothing else, so there's no server to fall back on, and a picker asks for exactly what can be
/// sent rather than inviting a phrase that would have to be refused.
/// </remarks>
internal sealed class DeadlineForm : Form
{
    private readonly DateTimePicker _picker;
    private readonly Button _clear;

    private bool _cleared;

    private DeadlineForm(string task, DateOnly? current, DateOnly today)
    {
        Text = "Deadline";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 156);

        var prompt = new Label { Text = "Finish it by:", Location = new Point(14, 14), Size = new Size(392, 20) };

        var heading = new Label
        {
            Text = task,
            Location = new Point(14, 36),
            Size = new Size(392, 20),
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,

            // What a task is called is the account's text, so an ampersand in it is a character
            // rather than the mark of an accelerator.
            UseMnemonic = false,
        };

        // Opens on the deadline it has, or on today for a task with none — which is the day
        // somebody reaching for this is most often counting from.
        _picker = new DateTimePicker
        {
            Location = new Point(14, 62),
            Size = new Size(392, 27),
            Format = DateTimePickerFormat.Long,
            Value = (current ?? today).ToDateTime(TimeOnly.MinValue),
        };

        // Shown disabled rather than hidden on a task without one: the button says what can be done
        // to a deadline, and a row of buttons that changes shape is one nobody can learn.
        _clear = new Button
        {
            Text = "Clear",
            Location = new Point(14, 108),
            Size = new Size(88, 30),
            Enabled = current is not null,
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(226, 108), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(318, 108), Size = new Size(88, 30) };

        _clear.Click += (_, _) =>
        {
            _cleared = true;
            DialogResult = DialogResult.OK;
        };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([prompt, heading, _picker, _clear, ok, cancel]);
    }

    /// <summary>The day picked, or null when the deadline is to be cleared.</summary>
    internal DateOnly? Chosen => _cleared ? null : DateOnly.FromDateTime(_picker.Value);

    /// <summary>Whether clearing is offered, which it isn't on a task with no deadline to clear.</summary>
    internal bool CanClear => _clear.Enabled;

    /// <summary>The dialog as it will be shown, built and not shown, for a test to look at.</summary>
    internal static DeadlineForm For(string task, DateOnly? current, DateOnly today) => new(task, current, today);

    /// <summary>
    /// Presses Clear, for a test with no dialog to click.
    /// </summary>
    /// <remarks>
    /// Raises the button's own Click rather than calling what it's wired to, so the wiring is part
    /// of what's covered. Not <c>PerformClick</c>: that goes through the button's selectability,
    /// which a window nobody has shown hasn't got.
    /// </remarks>
    internal void PressClear() => InvokeOnClick(_clear, EventArgs.Empty);

    /// <summary>
    /// Asks for the day a task has to be finished by.
    /// </summary>
    /// <param name="owner">The window to centre on</param>
    /// <param name="task">What the task is called, shown above the calendar</param>
    /// <param name="current">The deadline the task has now, or null when it hasn't got one</param>
    /// <param name="today">Today in the account's timezone, which a task with no deadline opens on</param>
    /// <param name="chosen">The day picked, or null to clear the deadline</param>
    /// <returns>True when a day was picked or cleared, false when the dialog was cancelled</returns>
    internal static bool Ask(IWin32Window owner, string task, DateOnly? current, DateOnly today, out DateOnly? chosen)
    {
        using var dialog = For(task, current, today);

        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            chosen = null;
            return false;
        }

        chosen = dialog.Chosen;
        return true;
    }
}
