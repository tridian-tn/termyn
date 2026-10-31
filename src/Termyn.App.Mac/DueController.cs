using ObjCRuntime;
using Termyn.Core.Capture;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The popover a task's due date is set in: Todoist's own words in a field, what they'll be read
/// as underneath, the days a click away, and a calendar.
/// </summary>
/// <remarks>
/// The field is the Windows due box — the same reading as you type, the same refusal to write
/// when what's typed means the day it already had. The buttons are the Schedule menu, and the
/// calendar moves the task to a day keeping its time and its repeat, as the menu does.
/// </remarks>
internal sealed class DueController(MainPresenter presenter, TaskCommands commands, string id, Action close) : NSViewController
{
    private static readonly AppCommand[] Days =
    [
        AppCommand.DueToday,
        AppCommand.DueTomorrow,
        AppCommand.DueWeekend,
        AppCommand.DueNextWeek,
        AppCommand.DueNone,
    ];

    private readonly NSTextField _field = new();
    private readonly NSTextField _verdict = NSTextField.CreateLabel(string.Empty);

    /// <summary>What the field opened on, so closing on the same words writes nothing.</summary>
    private string _opened = string.Empty;

    public override void LoadView()
    {
        var row = commands.Row(id);

        var name = NSTextField.CreateLabel(row?.Content ?? string.Empty);
        name.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize)!;
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        _opened = presenter.DueWritten(id);
        _field.StringValue = _opened;
        _field.PlaceholderString = DayBoxText.Hint(presenter.DatesInEnglish);
        _field.Delegate = new Typing(this);

        _verdict.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        ShowVerdict();

        var buttons = Days.Select(Button).ToArray();
        var first = Stack(NSUserInterfaceLayoutOrientation.Horizontal, 6, buttons[..3]);
        var second = Stack(NSUserInterfaceLayoutOrientation.Horizontal, 6, buttons[3..]);

        var calendar = new NSDatePicker
        {
            DatePickerStyle = NSDatePickerStyle.ClockAndCalendar,
            DatePickerElements = NSDatePickerElementFlags.YearMonthDate,
            DateValue = Date(row?.DueOn ?? presenter.Today),
            Bordered = false,
            DrawsBackground = false,
        };
        calendar.Activated += (_, _) =>
        {
            commands.Guarded(() => presenter.Reschedule(id, Day(calendar.DateValue)));
            close();
        };

        var stack = Stack(NSUserInterfaceLayoutOrientation.Vertical, 8, name, _field, _verdict, first, second, calendar);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(14, 14, 14, 14);
        foreach (var wide in new NSView[] { name, _field })
            wide.WidthAnchor.ConstraintEqualTo(280).Active = true;

        View = stack;
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        View.Window?.MakeFirstResponder(_field);
    }

    /// <summary>Closes the popover, for the field's Esc.</summary>
    private void Finish() => close();

    /// <summary>A day the Schedule menu offers, as a button that sets it and closes.</summary>
    private NSButton Button(AppCommand command)
    {
        var state = commands.State(command, id);
        var button = NSButton.CreateButton(TaskCommands.Label(command, state, commands.Row(id)), () =>
        {
            commands.Run(command, id);
            close();
        });
        button.ControlSize = NSControlSize.Small;
        button.Enabled = state.Enabled;
        button.ToolTip = state.Hint;
        return button;
    }

    private void ShowVerdict()
    {
        var verdict = DayBoxText.ForDue(presenter.ReadDay(_field.StringValue));
        _verdict.StringValue = verdict.Says;
        _verdict.TextColor = verdict.Accepted ? NSColor.SecondaryLabel : NSColor.SystemRed;
    }

    /// <summary>
    /// Writes what's typed, unless it's what the field opened on or other words for the same day:
    /// writing anyway would queue a change to Todoist, and a sync, for nothing.
    /// </summary>
    private void Apply()
    {
        var answer = _field.StringValue;
        var same = answer.Trim() == _opened.Trim()
            || (presenter.ReadDay(answer) is { Kind: DayReadingKind.Day } read && read == presenter.ReadDay(_opened));

        if (!same)
            commands.Guarded(() => presenter.SetDueFromText(id, answer));

        close();
    }

    /// <summary>A day, as the calendar shows it: noon on it, in this Mac's calendar.</summary>
    private static NSDate Date(DateOnly day)
        => NSCalendar.CurrentCalendar.DateFromComponents(new NSDateComponents { Year = day.Year, Month = day.Month, Day = day.Day, Hour = 12 })!;

    /// <summary>The day the calendar has picked, in this Mac's calendar.</summary>
    private static DateOnly Day(NSDate date)
    {
        var parts = NSCalendar.CurrentCalendar.Components(NSCalendarUnit.Year | NSCalendarUnit.Month | NSCalendarUnit.Day, date);
        return new DateOnly((int)parts.Year, (int)parts.Month, (int)parts.Day);
    }

    private static NSStackView Stack(NSUserInterfaceLayoutOrientation orientation, nfloat spacing, params NSView[] views)
    {
        var stack = NSStackView.FromViews(views);
        stack.Orientation = orientation;
        stack.Spacing = spacing;
        return stack;
    }

    private sealed class Typing(DueController owner) : NSTextFieldDelegate
    {
        public override void Changed(NSNotification notification) => owner.ShowVerdict();

        public override bool DoCommandBySelector(NSControl control, NSTextView textView, Selector commandSelector)
        {
            switch (commandSelector.Name)
            {
                case "insertNewline:":
                    owner.Apply();
                    return true;

                case "cancelOperation:":
                    owner.Finish();
                    return true;

                default:
                    return false;
            }
        }
    }
}
