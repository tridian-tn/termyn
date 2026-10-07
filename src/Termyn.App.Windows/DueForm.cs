using System.Globalization;
using Termyn.Core.Capture;
using Termyn.Presentation;

using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// When a task is due: a day typed or picked, and a time on it if it has one.
/// </summary>
/// <remarks>
/// <para>
/// The text is still the main way in, since a repeat can only be written — "every Monday" has no
/// day to pick. Anything the grammar here doesn't read goes to Todoist as the words, which is how a
/// repeat gets set at all. The calendar and the time field are for a day you'd rather see than
/// spell.
/// </para>
/// <para>
/// Both write into the box rather than keeping answers of their own, so what's typed is always the
/// whole of the answer. The time field follows the box the other way: it shows the time the box
/// reads as, and is greyed while the box isn't a day, since a repeat carries its time in its own
/// words and there's nothing else to put a time on.
/// </para>
/// </remarks>
internal sealed class DueForm : Form
{
    /// <summary>The time a ticked time field starts on, which is Todoist's own "in the morning".</summary>
    private static readonly TimeOnly Morning = new(9, 0);

    /// <summary>
    /// The day the time field's value is held on. Only its time is ever read, and it has to be a day
    /// the control can hold.
    /// </summary>
    private static readonly DateTime AnyDay = new(2000, 1, 1);

    private readonly DayBox _box;
    private readonly DateTimePicker _time;
    private readonly Button _clear;

    private bool _cleared;

    /// <summary>Whether the time field is being set to follow the box, rather than by a person.</summary>
    private bool _following;

    private DueForm(string task, string current, DateOnly today, Func<string, DayReading> read, bool english)
    {
        Text = "Due date";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 206);

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
            Text = "When is it due?",
            Location = new Point(14, 38),
            Size = new Size(392, 20),
            ForeColor = SystemColors.GrayText,
        };

        _box = new DayBox(read, DayBoxText.ForDue, today, takesTime: true, english)
        {
            Location = new Point(14, 62),
        };

        var at = new Label
        {
            Text = "Time:",
            Location = new Point(14, 128),
            Size = new Size(44, 20),
        };

        _time = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern,
            ShowUpDown = true,
            ShowCheckBox = true,
            Location = new Point(62, 124),
            Size = new Size(120, 27),
            AccessibleName = "Time",
            Value = AnyDay + Morning.ToTimeSpan(),
            Checked = false,
        };

        // Shown disabled rather than hidden on a task without one: the button says what can be done
        // to a due date, and a row of buttons that changes shape is one nobody can learn.
        _clear = new Button
        {
            Text = "Clear",
            Location = new Point(14, 164),
            Size = new Size(88, 30),
            Enabled = current.Length > 0,
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(226, 164), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(318, 164), Size = new Size(88, 30) };

        _clear.Click += (_, _) =>
        {
            _cleared = true;
            DialogResult = DialogResult.OK;
        };

        _box.ReadingChanged += (_, _) => Follow();
        _time.ValueChanged += (_, _) => TimeChanged();

        // Again once there's a native field to send the time to, which there isn't while it's built.
        _time.HandleCreated += (_, _) => Follow();

        _box.Typed = current;
        Follow();

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([heading, prompt, _box, at, _time, _clear, ok, cancel]);

        ActiveControl = _box.Entry;
        Shown += (_, _) => _box.Entry.SelectAll();
    }

    /// <summary>What the due date is to be, as words the presenter reads — empty to clear it.</summary>
    internal string Answer => _cleared ? string.Empty : _box.Typed;

    /// <summary>The box the day is asked for in.</summary>
    internal DayBox Box => _box;

    /// <summary>Whether the time field can be used, which it can while the box reads as a day.</summary>
    internal bool TimeOffered => _time.Enabled;

    /// <summary>The time the field shows, or null when it's unticked.</summary>
    internal TimeOnly? TimeShown => _time.Checked ? TimeOnly.FromDateTime(_time.Value) : null;

    /// <summary>Whether clearing is offered, which it isn't on a task with no due date to clear.</summary>
    internal bool CanClear => _clear.Enabled;

    /// <summary>The dialog as it will be shown, built and not shown, for a test to look at.</summary>
    /// <param name="task">What the task is called</param>
    /// <param name="current">The due date as the box opens on it, or empty when there isn't one</param>
    /// <param name="today">Today, which the calendar opens on when the box doesn't name a day</param>
    /// <param name="read">Reads what's typed into the box</param>
    /// <param name="english">Whether the account reads a typed date in English</param>
    /// <returns>The dialog, which the caller disposes</returns>
    internal static DueForm For(string task, string current, DateOnly today, Func<string, DayReading> read, bool english)
        => new(task, current, today, read, english);

    /// <summary>
    /// Sets the time field as a person would, for a test with no field to click.
    /// </summary>
    /// <remarks>
    /// Ticking the field's box doesn't raise anything when it's set from code, so this goes on to
    /// what the field's change is wired to — with the field already holding what a person would
    /// have left it on, so that's what gets read.
    /// </remarks>
    /// <param name="time">The time, or null to untick it</param>
    internal void ChooseTime(TimeOnly? time)
    {
        if (time is { } chosen)
            _time.Value = AnyDay + chosen.ToTimeSpan();

        _time.Checked = time is not null;
        TimeChanged();
    }

    /// <summary>
    /// Presses Clear, for a test with no dialog to click.
    /// </summary>
    /// <remarks>
    /// Raises the button's own Click rather than calling what it's wired to, so the wiring is part
    /// of what's covered.
    /// </remarks>
    internal void PressClear() => InvokeOnClick(_clear, EventArgs.Empty);

    /// <summary>Sets the time field to what the box reads as, and offers it only while that's a day.</summary>
    private void Follow()
    {
        _following = true;
        try
        {
            var reading = _box.Reading;
            _time.Enabled = reading.Kind is DayReadingKind.Day;

            if (reading.Time is { } time)
                _time.Value = AnyDay + time.ToTimeSpan();

            // The native field shows only a time it's been sent, and one built unticked has never
            // been sent any: it shows the moment it was made, greyed, and ticking it starts there.
            // Ticking it sends it the time it holds — the morning, or the last one the box had — and
            // that's what it goes on showing, and what ticking it starts on, once it's unticked
            // again. Unticked first, since a native field starts ticked and ticking one that already
            // is sends nothing.
            if (reading.Time is null && _time.IsHandleCreated)
            {
                _time.Checked = false;
                _time.Checked = true;
            }

            _time.Checked = reading.Time is not null;
        }
        finally
        {
            _following = false;
        }
    }

    /// <summary>Writes a time set in the field into the box, or takes it out when the field is unticked.</summary>
    private void TimeChanged()
    {
        if (_following)
            return;

        _box.SetTime(_time.Checked ? TimeOnly.FromDateTime(_time.Value) : null);
    }

    /// <summary>
    /// Asks when a task is due.
    /// </summary>
    /// <param name="owner">The window to centre on</param>
    /// <param name="task">What the task is called, shown above the question</param>
    /// <param name="current">The due date as the box opens on it, or empty when there isn't one</param>
    /// <param name="today">Today in the account's timezone, which the calendar opens on</param>
    /// <param name="read">Reads what's typed into the box, as quick add would</param>
    /// <param name="english">Whether the account reads a typed date in English</param>
    /// <returns>What the due date is to be, empty to clear it, or null when the dialog was cancelled</returns>
    internal static string? Ask(IWin32Window owner, string task, string current, DateOnly today, Func<string, DayReading> read, bool english)
    {
        using var dialog = For(task, current, today, read, english);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Answer : null;
    }
}
