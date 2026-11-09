using Termyn.Core.Capture;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The popover a task's due date is set in: the field and calendar every day has, and the days a
/// click away.
/// </summary>
/// <remarks>
/// The field is the Windows due box — the same reading as you type, the same refusal to write
/// when what's typed means the day it already had. The buttons are the Schedule menu, and the
/// calendar moves the task to a day keeping its time and its repeat, as the menu does.
/// </remarks>
internal sealed class DueController(MainPresenter presenter, TaskCommands commands, string id, Action close) : DayPopoverController(close)
{
    private static readonly AppCommand[] Days =
    [
        AppCommand.DueToday,
        AppCommand.DueTomorrow,
        AppCommand.DueWeekend,
        AppCommand.DueNextWeek,
        AppCommand.DueNone,
    ];

    /// <summary>What the field opened on, so closing on the same words writes nothing.</summary>
    private string _opened = string.Empty;

    public override void LoadView()
    {
        var row = commands.Row(id);
        _opened = presenter.DueWritten(id);

        var buttons = Days.Select(Button).ToArray();
        var first = Stack(NSUserInterfaceLayoutOrientation.Horizontal, 6, buttons[..3]);
        var second = Stack(NSUserInterfaceLayoutOrientation.Horizontal, 6, buttons[3..]);

        View = Lay(
            row?.Content ?? string.Empty,
            prompt: null,
            _opened,
            DayBoxText.Hint(presenter.DatesInEnglish),
            row?.DueOn ?? presenter.Today,
            first,
            second);
    }

    protected override DayVerdict Verdict() => DayBoxText.ForDue(presenter.ReadDay(Field.StringValue));

    /// <summary>
    /// Writes what's typed, unless it's what the field opened on or other words for the same day:
    /// writing anyway would queue a change to Todoist, and a sync, for nothing.
    /// </summary>
    protected override void Confirm()
    {
        var answer = Field.StringValue;
        var same = answer.Trim() == _opened.Trim()
            || (presenter.ReadDay(answer) is { Kind: DayReadingKind.Day } read && read == presenter.ReadDay(_opened));

        if (!same)
            commands.Guarded(() => presenter.SetDueFromText(id, answer));

        Close();
    }

    protected override void Picked(DateOnly day)
    {
        commands.Guarded(() => presenter.Reschedule(id, day));
        Close();
    }

    /// <summary>A day the Schedule menu offers, as a button that sets it and closes.</summary>
    private NSButton Button(AppCommand command)
    {
        var state = commands.State(command, id);
        var button = NSButton.CreateButton(TaskCommands.Label(command, state, commands.Row(id)), () =>
        {
            commands.Run(command, id);
            Close();
        });
        button.ControlSize = NSControlSize.Small;
        button.Enabled = state.Enabled;
        button.ToolTip = state.Hint;
        return button;
    }
}
