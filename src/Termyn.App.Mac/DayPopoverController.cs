using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// A popover a task's day is set in: Todoist's own words in a field, what they'll be read as
/// underneath, and a calendar.
/// </summary>
/// <remarks>
/// Return is the field's to answer, and Esc leaves the task as it was. Each kind of day says for
/// itself what it will take and how it's written.
/// </remarks>
/// <param name="close">Closes the popover</param>
internal abstract class DayPopoverController(Action close) : NSViewController
{
    private const int Margin = 14;
    private const int Width = 308;

    private readonly NSTextField _verdict = PopoverParts.SmallLine(Width - (2 * Margin));

    protected NSTextField Field { get; } = new();

    /// <summary>What the line under the field says of what's been typed.</summary>
    protected abstract DayVerdict Verdict();

    /// <summary>What Return does.</summary>
    protected abstract void Confirm();

    /// <summary>What a click on a day in the calendar does.</summary>
    /// <param name="day">The day</param>
    protected abstract void Picked(DateOnly day);

    protected void Close() => close();

    /// <summary>Lays the popover out, ready to be the view.</summary>
    /// <param name="heading">The task's name</param>
    /// <param name="prompt">What's being asked, when the heading and the field don't say, or null</param>
    /// <param name="written">The day as it stands, in words the field reads back</param>
    /// <param name="hint">What the field suggests typing while it's empty</param>
    /// <param name="shown">The day the calendar opens on</param>
    /// <param name="between">What goes between the field and the calendar</param>
    protected NSStackView Lay(string heading, string? prompt, string written, string hint, DateOnly shown, params NSView[] between)
    {
        var name = PopoverParts.Heading(heading);

        Field.StringValue = written;
        Field.PlaceholderString = hint;
        Field.Delegate = new FieldKeys(ShowVerdict, Confirm, close);

        ShowVerdict();

        var calendar = new NSDatePicker
        {
            DatePickerStyle = NSDatePickerStyle.ClockAndCalendar,
            DatePickerElements = NSDatePickerElementFlags.YearMonthDateDay,
            DateValue = Date(shown),
            Bordered = false,
            DrawsBackground = false,
        };
        calendar.Activated += (_, _) => Picked(Day(calendar.DateValue));

        NSView[] above = prompt is null ? [name] : [name, PopoverParts.Prompt(prompt)];
        var stack = Stack(NSUserInterfaceLayoutOrientation.Vertical, 8, [.. above, Field, _verdict, .. between, calendar]);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(Margin, Margin, Margin, Margin);

        // The stack's own width as well as what's in it: left to work it out from them, it comes up
        // short of the margins, and they sit hard against the popover's edge.
        stack.WidthAnchor.ConstraintEqualTo(Width).Active = true;
        foreach (var wide in new NSView[] { name, Field, _verdict })
            wide.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -2 * Margin).Active = true;

        return stack;
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        View.Window?.MakeFirstResponder(Field);
    }

    protected static NSStackView Stack(NSUserInterfaceLayoutOrientation orientation, nfloat spacing, params NSView[] views)
    {
        var stack = NSStackView.FromViews(views);
        stack.Orientation = orientation;
        stack.Spacing = spacing;
        return stack;
    }

    private void ShowVerdict()
    {
        var verdict = Verdict();
        _verdict.StringValue = verdict.Says;
        _verdict.TextColor = verdict.Accepted ? NSColor.SecondaryLabel : NSColor.SystemRed;
    }

    /// <summary>
    /// A day, as the calendar shows it: noon on it, whichever calendar this Mac uses.
    /// </summary>
    /// <remarks>
    /// A <see cref="DateOnly"/> counts its years the Gregorian way, so it's turned into a moment
    /// that way too. Asking the Mac's own calendar would read 2026 as a year of whichever one
    /// that is. The picker shows the moment in the Mac's calendar by itself.
    /// </remarks>
    private static NSDate Date(DateOnly day)
        => new NSCalendar(NSCalendarType.Gregorian).DateFromComponents(new NSDateComponents { Year = day.Year, Month = day.Month, Day = day.Day, Hour = 12 })!;

    /// <summary>The day the calendar has picked, counted as a <see cref="DateOnly"/> counts it.</summary>
    private static DateOnly Day(NSDate date)
    {
        var parts = new NSCalendar(NSCalendarType.Gregorian).Components(NSCalendarUnit.Year | NSCalendarUnit.Month | NSCalendarUnit.Day, date);
        return new DateOnly((int)parts.Year, (int)parts.Month, (int)parts.Day);
    }
}
