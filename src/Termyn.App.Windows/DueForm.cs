using System.ComponentModel;
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
/// <para>
/// The Repeat list is the same again: picking one writes its words into the box, the way Todoist's
/// own scheduler offers them, and it shows which one the box holds. A repeat set in Todoist that
/// isn't one of them shows as Custom, with its words in the box as they've always been, and
/// Custom… opens <see cref="RepeatForm"/> to build any other. The list is greyed for an account
/// that reads another language, since the words it writes are English.
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
    private readonly ComboBox _repeat;
    private readonly Button _clear;
    private readonly DateOnly _today;
    private readonly bool _english;

    private bool _cleared;

    /// <summary>Whether the time field is being set to follow the box, rather than by a person.</summary>
    private bool _following;

    /// <summary>Whether the Repeat list is being set to follow the box, rather than by a person.</summary>
    private bool _choosing;

    /// <summary>
    /// The day the Repeat list's choices are built on: the day the box last read as, or the day a
    /// repeat in it starts on, or the day the task is due when the box has said neither.
    /// </summary>
    private DateOnly _anchor;

    /// <summary>The repeats the Repeat list offers after its first entry, for the day they're built on.</summary>
    private RepeatRule[] _choices = [];

    private DueForm(string task, string current, DateOnly today, Func<string, DayReading> read, bool english, DateOnly? due)
    {
        _today = today;
        _english = english;
        _anchor = due ?? today;
        AskForRepeat = (rule, day, time, unreadable) => RepeatForm.Ask(this, rule, day, time, unreadable, today);

        Text = Strings.DueTitle;
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 244);

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
            Text = Strings.DuePrompt,
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
            Text = Strings.DueTime,
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
            AccessibleName = Strings.DueTimeAccessible,
            Value = AnyDay + Morning.ToTimeSpan(),
            Checked = false,
        };

        var every = new Label
        {
            Text = Strings.DueRepeat,
            Location = new Point(14, 166),
            Size = new Size(56, 20),
        };

        _repeat = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(74, 162),
            Size = new Size(240, 28),
            AccessibleName = Strings.DueRepeatAccessible,
            Enabled = english,
        };

        // Shown disabled rather than hidden on a task without one: the button says what can be done
        // to a due date, and a row of buttons that changes shape is one nobody can learn.
        _clear = new Button
        {
            Text = Strings.DueClear,
            Location = new Point(14, 202),
            Size = new Size(88, 30),
            Enabled = current.Length > 0,
        };

        var ok = new Button { Text = Strings.ButtonOK, DialogResult = DialogResult.OK, Location = new Point(226, 202), Size = new Size(88, 30) };
        var cancel = new Button { Text = Strings.ButtonCancel, DialogResult = DialogResult.Cancel, Location = new Point(318, 202), Size = new Size(88, 30) };

        _clear.Click += (_, _) =>
        {
            _cleared = true;
            DialogResult = DialogResult.OK;
        };

        _box.ReadingChanged += (_, _) => Follow();
        _time.ValueChanged += (_, _) => TimeChanged();
        _repeat.SelectedIndexChanged += (_, _) => RepeatChosen();

        // Again once there's a native field to send the time to, which there isn't while it's built.
        _time.HandleCreated += (_, _) => Follow();

        _box.Typed = current;
        Follow();

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([heading, prompt, _box, at, _time, every, _repeat, _clear, ok, cancel]);

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

    /// <summary>The list of repeats, which a test picks from by setting its index.</summary>
    internal ComboBox Repeat => _repeat;

    /// <summary>The dialog as it will be shown, built and not shown, for a test to look at.</summary>
    /// <param name="task">What the task is called</param>
    /// <param name="current">The due date as the box opens on it, or empty when there isn't one</param>
    /// <param name="today">Today, which the calendar opens on when the box doesn't name a day</param>
    /// <param name="read">Reads what's typed into the box</param>
    /// <param name="english">Whether the account reads a typed date in English</param>
    /// <param name="due">The day the task is due on now, or null when it has none</param>
    /// <returns>The dialog, which the caller disposes</returns>
    internal static DueForm For(string task, string current, DateOnly today, Func<string, DayReading> read, bool english, DateOnly? due = null)
        => new(task, current, today, read, english, due);

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

        FollowRepeat();
    }

    /// <summary>
    /// Builds the Repeat list on the day the box names and selects the entry the box holds.
    /// </summary>
    /// <remarks>
    /// An entry matches when it writes the same words as the box once both are read, so "every
    /// saturday" typed out shows as the entry that writes "every sat". Any other repeat gets an
    /// entry of its own above Custom…, holding its words as Todoist has them, so a repeat set there
    /// is shown for what it is — and so Custom… is still something to pick rather than the entry
    /// already picked, which a list doesn't let you pick again. Anything that isn't a repeat is no
    /// repeat at all.
    /// </remarks>
    private void FollowRepeat()
    {
        var reading = _box.Reading;
        var rule = reading.Kind is DayReadingKind.Repeat ? RepeatWords.Read(_box.Typed, _today) : null;

        if (reading is { Kind: DayReadingKind.Day, Day: { } day })
            _anchor = day;
        else if (rule?.Start is { } start)
            _anchor = start;

        _choosing = true;
        try
        {
            _choices = Choices(_anchor);

            var matched = reading.Kind is DayReadingKind.Repeat && rule is not null
                ? Array.FindIndex(_choices, c => Same(c with { Time = rule.Time }, rule))
                : -1;

            var labels = new List<string> { Strings.RepeatNone };
            labels.AddRange(_choices.Select(Label));

            if (reading.Kind is DayReadingKind.Repeat && matched < 0)
                labels.Add(_box.Typed.Trim());

            labels.Add(Strings.RepeatCustom);

            if (!_repeat.Items.Cast<string>().SequenceEqual(labels))
            {
                _repeat.Items.Clear();
                _repeat.Items.AddRange([.. labels]);
            }

            _repeat.SelectedIndex = reading.Kind is not DayReadingKind.Repeat
                ? 0
                : matched >= 0
                    ? matched + 1
                    : _repeat.Items.Count - 2;
        }
        finally
        {
            _choosing = false;
        }
    }

    /// <summary>Writes the repeat picked from the list into the box, or takes one out.</summary>
    private void RepeatChosen()
    {
        if (_choosing)
            return;

        var index = _repeat.SelectedIndex;
        var reading = _box.Reading;

        // The time goes with it either way: into the repeat's words, or back onto the one day.
        var time = reading.Kind is DayReadingKind.Repeat ? RepeatWords.Read(_box.Typed, _today)?.Time : reading.Time;

        if (index == 0)
        {
            if (reading.Kind is DayReadingKind.Repeat)
                _box.Typed = QuickAddParser.Written(_anchor, time, _english);
        }
        else if (index == _repeat.Items.Count - 1)
        {
            ChooseCustom();
        }
        else if (index > 0 && index <= _choices.Length)
        {
            _box.Typed = RepeatWords.Write(_choices[index - 1] with { Time = time }, _today);
        }
    }

    /// <summary>
    /// Asks for a repeat built in parts. A property so a test can answer without a dialog to click;
    /// the dialog sets it to the real one as it's built.
    /// </summary>
    /// <remarks>
    /// Given the repeat the box holds when it's one that can be taken apart, the day and time to
    /// build a new one on, and the box's words when they're a repeat that can't be; answers with
    /// the repeat, or null when it was cancelled.
    /// </remarks>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<RepeatRule?, DateOnly, TimeOnly?, string?, RepeatRule?> AskForRepeat { get; set; }

    /// <summary>
    /// Custom from the Repeat list: the dialog that builds a repeat in parts, opened on the one in
    /// the box, and its words written into the box when it's saved.
    /// </summary>
    private void ChooseCustom()
    {
        var reading = _box.Reading;
        var repeats = reading.Kind is DayReadingKind.Repeat;
        var rule = repeats ? RepeatWords.Read(_box.Typed, _today) : null;
        var time = repeats ? rule?.Time : reading.Time;
        var unreadable = repeats && rule is null ? _box.Typed.Trim() : null;

        if (AskForRepeat(rule, _anchor, time, unreadable) is { } built)
            _box.Typed = RepeatWords.Write(built, _today);

        // Again whether or not anything was written: cancelled, the list goes back to what the box
        // holds, and saved on words the box already had, it never heard the box change.
        FollowRepeat();
    }

    /// <summary>
    /// The repeats the list offers, built on a day the way Todoist's scheduler builds its own.
    /// </summary>
    /// <param name="day">The day they're built on</param>
    /// <returns>Every day, every week on its weekday, every weekday, every month on its date, every year on it</returns>
    internal static RepeatRule[] Choices(DateOnly day) =>
    [
        new(RepeatUnit.Day, Start: day),
        new(RepeatUnit.Week, Weekdays: [day.DayOfWeek], Start: day),

        // From the day itself on a working day, and from wherever the words begin on a weekend,
        // since a repeat on working days can't start on a Saturday.
        new(
            RepeatUnit.Week,
            Weekdays: [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            Start: day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? null : day),

        new(RepeatUnit.Month, Start: day),
        new(RepeatUnit.Year, Start: day),
    ];

    /// <summary>What the list calls a repeat it offers.</summary>
    /// <param name="rule">One of <see cref="Choices"/></param>
    /// <returns>Its label, in the machine's language</returns>
    private static string Label(RepeatRule rule)
    {
        var culture = CultureInfo.CurrentCulture;
        var day = rule.Start ?? default;

        return rule switch
        {
            { Unit: RepeatUnit.Day } => Strings.RepeatDaily,
            { Unit: RepeatUnit.Week, Days.Count: 1 } => string.Format(culture, Strings.RepeatWeekly, culture.DateTimeFormat.GetDayName(rule.Days[0])),
            { Unit: RepeatUnit.Week } => Strings.RepeatWeekdays,
            { Unit: RepeatUnit.Month } => string.Format(culture, Strings.RepeatMonthly, day.Day),
            _ => string.Format(culture, Strings.RepeatYearly, DisplayDates.DayMonth(day, culture)),
        };
    }

    /// <summary>Whether two repeats come out as the same words.</summary>
    private bool Same(RepeatRule one, RepeatRule other)
        => RepeatWords.Write(one, _today) == RepeatWords.Write(other, _today);

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
    /// <param name="due">The day the task is due on now, which a repeat picked off the list is built on</param>
    /// <returns>What the due date is to be, empty to clear it, or null when the dialog was cancelled</returns>
    internal static string? Ask(IWin32Window owner, string task, string current, DateOnly today, Func<string, DayReading> read, bool english, DateOnly? due)
    {
        using var dialog = For(task, current, today, read, english, due);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Answer : null;
    }
}
