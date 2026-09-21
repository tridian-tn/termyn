using System.Globalization;

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
///
/// The two ways of naming a date are to be unified, so the day sits here as plain text beside a
/// button that drops a calendar — not in a date control of its own, which is a shape the unified
/// one won't keep.
/// </remarks>
internal sealed class DeadlineForm : Form
{
    /// <summary>How the chosen day is written out: long enough to name the weekday.</summary>
    private const string Written = "dddd, d MMMM yyyy";

    /// <summary>How big the calendar on the button is drawn.</summary>
    private const int GlyphSize = 16;

    private readonly Label _shown;
    private readonly Button _pick;
    private readonly Button _clear;
    private readonly MonthCalendar _calendar;
    private readonly ToolStripDropDown _drop;
    private readonly Bitmap _glyph;

    private DateOnly _day;
    private bool _cleared;

    private DeadlineForm(string task, DateOnly? current, DateOnly today)
    {
        _day = current ?? today;

        Text = "Deadline";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 156);

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

        // Drawn into an image rather than painted on the button: a button in the system's own style
        // is drawn by Windows and never raises Paint, which left the glyph off it entirely.
        _glyph = new Bitmap(GlyphSize, GlyphSize);
        using (var into = Graphics.FromImage(_glyph))
            CalendarGlyph.Draw(into, new Rectangle(0, 0, GlyphSize, GlyphSize), SystemColors.ControlText);

        _pick = new Button
        {
            Location = new Point(14, 62),
            Size = new Size(32, 28),
            Image = _glyph,
            AccessibleName = "Pick a day",
        };

        _pick.Click += (_, _) => ShowCalendar();

        _shown = new Label
        {
            Location = new Point(52, 66),
            Size = new Size(354, 20),
            Text = _day.ToString(Written, CultureInfo.CurrentCulture),
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

        _calendar = new MonthCalendar { MaxSelectionCount = 1, SelectionStart = _day.ToDateTime(TimeOnly.MinValue) };
        _calendar.DateSelected += (_, e) => Picked(DateOnly.FromDateTime(e.Start));

        // Arrow keys move the selection without picking anything, so the day on show follows them
        // too: the calendar is closed with Enter or Escape, and what it was left on is the answer.
        _calendar.DateChanged += (_, e) => Picked(DateOnly.FromDateTime(e.Start), keepOpen: true);
        _calendar.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Escape)
                _drop.Close();
        };

        _drop = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true };
        _drop.Items.Add(new ToolStripControlHost(_calendar) { Margin = Padding.Empty, Padding = Padding.Empty });

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([heading, prompt, _pick, _shown, _clear, ok, cancel]);

        FormClosed += (_, _) =>
        {
            _drop.Dispose();
            _glyph.Dispose();
        };
    }

    /// <summary>The day picked, or null when the deadline is to be cleared.</summary>
    internal DateOnly? Chosen => _cleared ? null : _day;

    /// <summary>The day as the dialog writes it out.</summary>
    internal string DayShown => _shown.Text;

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

    /// <summary>Takes a day off the calendar, as choosing one in it does.</summary>
    /// <param name="day">The day chosen</param>
    internal void PickFromCalendar(DateOnly day)
    {
        _calendar.SetDate(day.ToDateTime(TimeOnly.MinValue));
        Picked(day);
    }

    /// <summary>
    /// Takes the day chosen and says so on the face of the dialog.
    /// </summary>
    /// <param name="day">The day now chosen</param>
    /// <param name="keepOpen">Whether the calendar stays up, which it does while keys move it</param>
    private void Picked(DateOnly day, bool keepOpen = false)
    {
        _day = day;

        // Picking a day answers the same question Clear does, so it undoes a Clear pressed before it.
        _cleared = false;
        _shown.Text = day.ToString(Written, CultureInfo.CurrentCulture);

        if (!keepOpen)
            _drop.Close();
    }

    /// <summary>Drops the calendar under the button that asks for it.</summary>
    private void ShowCalendar()
    {
        _calendar.SetDate(_day.ToDateTime(TimeOnly.MinValue));
        _drop.Show(_pick, new Point(0, _pick.Height));
        _calendar.Focus();
    }

    /// <summary>
    /// Asks for the day a task has to be finished by.
    /// </summary>
    /// <param name="owner">The window to centre on</param>
    /// <param name="task">What the task is called, shown above the question</param>
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
