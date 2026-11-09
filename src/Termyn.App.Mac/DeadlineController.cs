using Termyn.Core.Capture;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The popover the day a task has to be finished by is set in.
/// </summary>
/// <remarks>
/// Asked for the same way as a due date, and differs only in what it will take. Todoist takes a
/// deadline as a date and nothing else, so what the field can't read is refused rather than sent,
/// and so is a time or a repeat, as in the Windows dialog. A refusal leaves the popover open with
/// the line under the field saying why.
/// </remarks>
internal sealed class DeadlineController(MainPresenter presenter, TaskCommands commands, string id, Action close) : DayPopoverController(close)
{
    /// <summary>The deadline when the popover opened, kept in case the task's row goes meanwhile.</summary>
    private DateOnly? _opened;

    public override void LoadView()
    {
        var row = commands.Row(id);
        _opened = row?.DeadlineOn;

        // Shown greyed rather than hidden on a task without one, as on Windows: it says what can be
        // done to a deadline.
        var clear = NSButton.CreateButton(Strings.DeadlineClear, () => Set(null));
        clear.ControlSize = NSControlSize.Small;
        clear.Enabled = _opened is not null;

        View = Lay(
            row?.Content ?? string.Empty,
            Strings.DeadlinePrompt,
            _opened is { } day ? QuickAddParser.Written(day, null, presenter.DatesInEnglish) : string.Empty,
            DayBoxText.Hint(presenter.DatesInEnglish),
            _opened ?? presenter.Today,
            clear);
    }

    protected override DayVerdict Verdict() => DayBoxText.ForDeadline(presenter.ReadDay(Field.StringValue), presenter.DatesInEnglish);

    protected override void Confirm()
    {
        if (Verdict().Accepted)
            Set(presenter.ReadDay(Field.StringValue).Day);
    }

    protected override void Picked(DateOnly day) => Set(day);

    /// <summary>
    /// Writes a deadline, unless it's the one the popover opened on: writing anyway would queue a
    /// change to Todoist, and a sync, for nothing.
    /// </summary>
    /// <remarks>
    /// The one it opened on rather than the one the task has now, as the due popover does. A change
    /// that came in by a sync meanwhile shouldn't be undone by Return on a field nobody touched.
    /// </remarks>
    /// <param name="day">The day, or null to clear it</param>
    private void Set(DateOnly? day)
    {
        if (day != _opened)
            commands.SetDeadline(id, day);

        Close();
    }
}
