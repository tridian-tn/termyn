using Termyn.Core.Capture;
using Termyn.Presentation;

using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// The day a task has to be finished by.
/// </summary>
/// <remarks>
/// Asked for the same way as a due date, with the same box and the same grammar, and differs only
/// in what it will take. The API takes a deadline as a date and nothing else, so there's no server
/// to hand words to: what the grammar here can't read is refused rather than sent, and so is a time
/// or a repeat, neither of which a deadline can hold.
/// </remarks>
internal sealed class DeadlineForm : Form
{
    private readonly DayBox _box;
    private readonly Button _clear;
    private readonly Button _ok;

    private bool _cleared;

    private DeadlineForm(string task, DateOnly? current, DateOnly today, Func<string, DayReading> read)
    {
        Text = "Deadline";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 170);

        // The task first: what's being changed, before what's being asked about it.
        var heading = new Label
        {
            Text = task,
            Location = new Point(14, 14),
            Size = new Size(392, 20),
            AutoEllipsis = true,

            // What a task is called is the account's text, so an ampersand in it is a character
            // rather than the mark of an accelerator.
            UseMnemonic = false,
        };

        var prompt = new Label
        {
            Text = "Finish it by:",
            Location = new Point(14, 38),
            Size = new Size(392, 20),
            ForeColor = SystemColors.GrayText,
        };

        _box = new DayBox(read, DayBoxText.ForDeadline, today, takesTime: false)
        {
            Location = new Point(14, 62),
            Typed = current is { } day ? QuickAddParser.Written(day) : string.Empty,
        };

        // Shown disabled rather than hidden on a task without one: the button says what can be done
        // to a deadline, and a row of buttons that changes shape is one nobody can learn.
        _clear = new Button
        {
            Text = "Clear",
            Location = new Point(14, 128),
            Size = new Size(88, 30),
            Enabled = current is not null,
        };

        _ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(226, 128), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(318, 128), Size = new Size(88, 30) };

        _clear.Click += (_, _) =>
        {
            _cleared = true;
            DialogResult = DialogResult.OK;
        };

        // Greyed while the box holds something a deadline can't be, so Enter can't send it. The line
        // under the box has already said why.
        _box.ReadingChanged += (_, _) => _ok.Enabled = _box.Verdict.Accepted;
        _ok.Enabled = _box.Verdict.Accepted;

        AcceptButton = _ok;
        CancelButton = cancel;
        Controls.AddRange([heading, prompt, _box, _clear, _ok, cancel]);

        ActiveControl = _box.Entry;
        Shown += (_, _) => _box.Entry.SelectAll();
    }

    /// <summary>The day settled on, or null when the deadline is to be cleared.</summary>
    internal DateOnly? Chosen => _cleared ? null : _box.Reading.Day;

    /// <summary>The box the day is asked for in.</summary>
    internal DayBox Box => _box;

    /// <summary>Whether OK will close the dialog with what's in the box.</summary>
    internal bool CanAccept => _ok.Enabled;

    /// <summary>Whether clearing is offered, which it isn't on a task with no deadline to clear.</summary>
    internal bool CanClear => _clear.Enabled;

    /// <summary>The dialog as it will be shown, built and not shown, for a test to look at.</summary>
    /// <param name="task">What the task is called</param>
    /// <param name="current">The deadline it has now, or null</param>
    /// <param name="today">Today, which the calendar opens on when the box doesn't name a day</param>
    /// <param name="read">Reads what's typed into the box</param>
    /// <returns>The dialog, which the caller disposes</returns>
    internal static DeadlineForm For(string task, DateOnly? current, DateOnly today, Func<string, DayReading> read)
        => new(task, current, today, read);

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
    /// <param name="task">What the task is called, shown above the question</param>
    /// <param name="current">The deadline the task has now, or null when it hasn't got one</param>
    /// <param name="today">Today in the account's timezone, which the calendar opens on</param>
    /// <param name="read">Reads what's typed into the box, as quick add would</param>
    /// <param name="chosen">The day settled on, or null to clear the deadline</param>
    /// <returns>True when a day was settled on or cleared, false when the dialog was cancelled</returns>
    internal static bool Ask(
        IWin32Window owner,
        string task,
        DateOnly? current,
        DateOnly today,
        Func<string, DayReading> read,
        out DateOnly? chosen)
    {
        using var dialog = For(task, current, today, read);

        if (dialog.ShowDialog(owner) != DialogResult.OK)
        {
            chosen = null;
            return false;
        }

        chosen = dialog.Chosen;
        return true;
    }
}
