using System.Globalization;
using Termyn.Core.Capture;
using Termyn.Presentation;

using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// A repeat built in parts, for Custom… in the due-date dialog's Repeat list.
/// </summary>
/// <remarks>
/// <para>
/// What Todoist's own Custom dialog asks: whether the next one is counted from the due date or from
/// the day it's ticked off, how often, and when it stops. A start day and a time go with them, since
/// both are part of the words here rather than fields of their own.
/// </para>
/// <para>
/// It answers with a <see cref="RepeatRule"/> and the words are written from that, which the line at
/// the bottom shows as they're built. A repeat set in Todoist in words this can't take apart opens
/// on the task's own day instead, with a line saying what it was and that saving replaces it.
/// </para>
/// <para>
/// The days of the week are offered only where Todoist has words for them, which is a repeat every
/// single week counted from the due date: "every mon, fri" is in its help, and "every 2 weeks on
/// mon" isn't.
/// </para>
/// </remarks>
internal sealed class RepeatForm : Form
{
    private static readonly DateTime AnyDay = new(2000, 1, 1);

    private static readonly TimeOnly Morning = new(9, 0);

    private readonly DateOnly _today;
    private readonly RadioButton _fromDue;
    private readonly RadioButton _fromCompletion;
    private readonly NumericUpDown _interval;
    private readonly ComboBox _unit;
    private readonly CheckBox[] _days;
    private readonly DateTimePicker _start;
    private readonly DateTimePicker _time;
    private readonly RadioButton _never;
    private readonly RadioButton _endsOn;
    private readonly DateTimePicker _until;
    private readonly Label _preview;
    private readonly Label _unreadable;

    /// <summary>Whether the unit list's wording is being changed to match the number, rather than picked.</summary>
    private bool _wording;

    private RepeatForm(RepeatRule? rule, DateOnly day, TimeOnly? time, string? unreadable, DateOnly today)
    {
        _today = today;

        Text = Strings.RepeatTitle;
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 362);

        _fromDue = new RadioButton { Text = Strings.RepeatFromDue, Location = new Point(14, 14), Size = new Size(200, 24), Checked = true };
        _fromCompletion = new RadioButton { Text = Strings.RepeatFromCompletion, Location = new Point(220, 14), Size = new Size(206, 24) };

        var every = new Label { Text = Strings.RepeatEvery, Location = new Point(14, 56), Size = new Size(56, 20) };
        _interval = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 999,
            Value = 1,
            Location = new Point(74, 52),
            Size = new Size(64, 27),
            AccessibleName = Strings.RepeatEveryAccessible,
        };

        _unit = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(144, 52),
            Size = new Size(120, 28),
            AccessibleName = Strings.RepeatUnitAccessible,
        };

        // In the order the machine's region starts its week, as a calendar there would show them.
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        _days = Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)(((int)first + i) % 7))
            .Select((d, i) => new CheckBox
            {
                Text = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d),
                Tag = d,
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(14 + (i * 59), 90),
                Size = new Size(55, 28),
                AccessibleName = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(d),
            })
            .ToArray();

        var starts = new Label { Text = Strings.RepeatStarts, Location = new Point(14, 136), Size = new Size(56, 20) };
        _start = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Location = new Point(74, 132),
            Size = new Size(160, 27),
            AccessibleName = Strings.RepeatStartsAccessible,
        };

        var at = new Label { Text = Strings.DueTime, Location = new Point(14, 172), Size = new Size(56, 20) };
        _time = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern,
            ShowUpDown = true,
            ShowCheckBox = true,
            Location = new Point(74, 168),
            Size = new Size(120, 27),
            AccessibleName = Strings.DueTimeAccessible,
        };

        var ends = new Label { Text = Strings.RepeatEnds, Location = new Point(14, 208), Size = new Size(56, 20) };
        _never = new RadioButton { Text = Strings.RepeatNever, Location = new Point(74, 206), Size = new Size(80, 24), Checked = true };
        _endsOn = new RadioButton { Text = Strings.RepeatEndsOn, Location = new Point(158, 206), Size = new Size(52, 24) };
        _until = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Location = new Point(214, 204),
            Size = new Size(160, 27),
            AccessibleName = Strings.RepeatEndsAccessible,
        };

        _preview = new Label { Location = new Point(14, 244), Size = new Size(412, 40), UseMnemonic = false };
        _unreadable = new Label
        {
            Location = new Point(14, 284),
            Size = new Size(412, 34),
            ForeColor = SystemColors.GrayText,
            UseMnemonic = false,
            Visible = unreadable is not null,
            Text = unreadable is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, Strings.RepeatUnreadable, unreadable),
        };

        var ok = new Button { Text = Strings.ButtonOK, DialogResult = DialogResult.OK, Location = new Point(246, 322), Size = new Size(88, 30) };
        var cancel = new Button { Text = Strings.ButtonCancel, DialogResult = DialogResult.Cancel, Location = new Point(338, 322), Size = new Size(88, 30) };

        Show(rule ?? new RepeatRule(RepeatUnit.Week, Weekdays: [day.DayOfWeek], Start: day, Time: time), day);

        // The native field shows only a time it's been sent, as the due-date dialog's own does, so
        // it's ticked once on the way to what it's meant to be, to send it one.
        _time.HandleCreated += (_, _) =>
        {
            var on = _time.Checked;
            _time.Checked = false;
            _time.Checked = true;
            _time.Checked = on;
        };

        _fromDue.CheckedChanged += (_, _) => Changed();
        _fromCompletion.CheckedChanged += (_, _) => Changed();
        _interval.ValueChanged += (_, _) => Changed();
        _unit.SelectedIndexChanged += (_, _) => Changed();
        _start.ValueChanged += (_, _) => Changed();
        _time.ValueChanged += (_, _) => Changed();
        _never.CheckedChanged += (_, _) => Changed();
        _endsOn.CheckedChanged += (_, _) => Changed();
        _until.ValueChanged += (_, _) => Changed();
        foreach (var box in _days)
            box.CheckedChanged += (_, _) => Changed();

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([_fromDue, _fromCompletion, every, _interval, _unit, .. _days, starts, _start, at, _time, ends, _never, _endsOn, _until, _preview, _unreadable, ok, cancel]);

        Changed();
    }

    /// <summary>The repeat as the dialog stands.</summary>
    internal RepeatRule Rule
    {
        get
        {
            var unit = (RepeatUnit)Math.Max(0, _unit.SelectedIndex);
            var days = unit is RepeatUnit.Week ? _days.Where(d => d.Checked).Select(d => (DayOfWeek)d.Tag!).ToArray() : [];

            return new RepeatRule(
                unit,
                (int)_interval.Value,
                days.Length > 0 ? days : null,
                DateOnly.FromDateTime(_start.Value),
                _endsOn.Checked ? DateOnly.FromDateTime(_until.Value) : null,
                _time.Checked ? TimeOnly.FromDateTime(_time.Value) : null,
                _fromCompletion.Checked);
        }
    }

    /// <summary>The words it will be sent as.</summary>
    internal string Words => RepeatWords.Write(Rule, _today);

    /// <summary>The line at the bottom saying what Todoist will read.</summary>
    internal string Preview => _preview.Text;

    /// <summary>Whether the days of the week can be picked, which they can only where Todoist has words for them.</summary>
    /// <remarks>Asked of the unit rather than of the boxes' Visible, which is false for any control on a dialog not yet shown.</remarks>
    internal bool DaysOffered => _unit.SelectedIndex == (int)RepeatUnit.Week && _days[0].Enabled;

    /// <summary>The line saying the task's own repeat can't be shown, or empty when it isn't there.</summary>
    internal string Unreadable => _unreadable.Text;

    /// <summary>The number box, which a test sets.</summary>
    internal NumericUpDown Interval => _interval;

    /// <summary>The unit list, which a test picks from by its index — days, weeks, months, years.</summary>
    internal ComboBox Unit => _unit;

    /// <summary>Ticks or unticks a day of the week, for a test with no button to press.</summary>
    /// <param name="day">The day</param>
    /// <param name="on">Whether it's ticked</param>
    internal void Tick(DayOfWeek day, bool on) => _days.Single(d => (DayOfWeek)d.Tag! == day).Checked = on;

    /// <summary>Counts from completion rather than from the due date, for a test.</summary>
    internal void CountFromCompletion() => _fromCompletion.Checked = true;

    /// <summary>Sets the last day it can fall on, or none, for a test.</summary>
    /// <param name="day">The day, or null for never</param>
    internal void EndOn(DateOnly? day)
    {
        if (day is { } last)
            _until.Value = last.ToDateTime(TimeOnly.MinValue);

        _endsOn.Checked = day is not null;
        _never.Checked = day is null;
    }

    /// <summary>The dialog as it will be shown, built and not shown, for a test to look at.</summary>
    /// <param name="rule">The repeat it opens on, or null to open on one built on the day</param>
    /// <param name="day">The day the task's due on, which a new repeat starts on</param>
    /// <param name="time">The time the task's due at, or null for the whole day</param>
    /// <param name="unreadable">The task's own repeat when it's in words this can't take apart, or null</param>
    /// <param name="today">Today in the account's timezone, which the words are counted from</param>
    /// <returns>The dialog, which the caller disposes</returns>
    internal static RepeatForm For(RepeatRule? rule, DateOnly day, TimeOnly? time, string? unreadable, DateOnly today)
        => new(rule, day, time, unreadable, today);

    /// <summary>
    /// Asks for a repeat.
    /// </summary>
    /// <param name="owner">The window to centre on</param>
    /// <param name="rule">The repeat it opens on, or null to open on one built on the day</param>
    /// <param name="day">The day the task's due on, which a new repeat starts on</param>
    /// <param name="time">The time the task's due at, or null for the whole day</param>
    /// <param name="unreadable">The task's own repeat when it's in words this can't take apart, or null</param>
    /// <param name="today">Today in the account's timezone</param>
    /// <returns>The repeat, or null when the dialog was cancelled</returns>
    internal static RepeatRule? Ask(IWin32Window owner, RepeatRule? rule, DateOnly day, TimeOnly? time, string? unreadable, DateOnly today)
    {
        using var dialog = For(rule, day, time, unreadable, today);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Rule : null;
    }

    /// <summary>Sets every control to a repeat.</summary>
    /// <param name="rule">The repeat</param>
    /// <param name="day">The day to start on when the repeat doesn't say</param>
    private void Show(RepeatRule rule, DateOnly day)
    {
        _fromCompletion.Checked = rule.FromCompletion;
        _fromDue.Checked = !rule.FromCompletion;
        _interval.Value = Math.Clamp(rule.Interval, (int)_interval.Minimum, (int)_interval.Maximum);
        Word();
        _unit.SelectedIndex = (int)rule.Unit;

        foreach (var box in _days)
            box.Checked = rule.Days.Contains((DayOfWeek)box.Tag!);

        _start.Value = (rule.Start ?? day).ToDateTime(TimeOnly.MinValue);
        _time.Value = AnyDay + (rule.Time ?? Morning).ToTimeSpan();
        _time.Checked = rule.Time is not null;

        _until.Value = (rule.Until ?? day.AddMonths(1)).ToDateTime(TimeOnly.MinValue);
        _endsOn.Checked = rule.Until is not null;
        _never.Checked = rule.Until is null;
    }

    /// <summary>Puts the unit list in the singular or the plural to go with the number before it.</summary>
    private void Word()
    {
        var plural = _interval.Value > 1;
        string[] words = plural
            ? [Strings.RepeatDays, Strings.RepeatWeeks, Strings.RepeatMonths, Strings.RepeatYears]
            : [Strings.RepeatDay, Strings.RepeatWeek, Strings.RepeatMonth, Strings.RepeatYear];

        if (_unit.Items.Cast<string>().SequenceEqual(words))
            return;

        _wording = true;
        try
        {
            var chosen = _unit.SelectedIndex;
            _unit.Items.Clear();
            _unit.Items.AddRange(words);
            _unit.SelectedIndex = Math.Max(0, chosen);
        }
        finally
        {
            _wording = false;
        }
    }

    /// <summary>Brings the rest of the dialog into line with what's just changed.</summary>
    private void Changed()
    {
        if (_wording)
            return;

        Word();

        var weekly = _unit.SelectedIndex == (int)RepeatUnit.Week;
        var named = weekly && _interval.Value == 1 && _fromDue.Checked;
        foreach (var box in _days)
        {
            box.Visible = weekly;
            box.Enabled = named;
        }

        _until.Enabled = _endsOn.Checked;
        _preview.Text = string.Format(CultureInfo.CurrentCulture, Strings.RepeatPreview, Words);
    }
}
