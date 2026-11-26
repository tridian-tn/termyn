using ObjCRuntime;

namespace Termyn.App.Mac;

/// <summary>What the popovers about a task are made of, so they read alike and stay that way.</summary>
internal static class PopoverParts
{
    /// <summary>What a popover's about, usually the task's name, cut short rather than widening it.</summary>
    public static NSTextField Heading(string words)
    {
        var heading = NSTextField.CreateLabel(words);
        heading.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize)!;
        heading.LineBreakMode = NSLineBreakMode.TruncatingTail;
        heading.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return heading;
    }

    /// <summary>A quiet label saying what a control beside or under it is asking.</summary>
    public static NSTextField Prompt(string words)
    {
        var prompt = NSTextField.CreateLabel(words);
        prompt.TextColor = NSColor.SecondaryLabel;
        return prompt;
    }

    /// <summary>A line of small text under a field, wrapping within a width rather than widening the popover.</summary>
    public static NSTextField SmallLine(int width)
    {
        var line = NSTextField.CreateWrappingLabel(string.Empty);
        line.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        line.Selectable = false;
        line.PreferredMaxLayoutWidth = width;
        return line;
    }

    /// <summary>
    /// A headerless list with one column, in a scroll view of its own. It refuses the keyboard, so a
    /// click on a row leaves the caret in the popover's field.
    /// </summary>
    public static NSScrollView List(NSTableView table, NSTableViewDataSource source, NSTableViewDelegate rows)
    {
        table.AddColumn(new NSTableColumn("Row") { ResizingMask = NSTableColumnResizing.Autoresizing });
        table.HeaderView = null;
        table.Style = NSTableViewStyle.Plain;
        table.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        table.RefusesFirstResponder = true;
        table.DataSource = source;
        table.Delegate = rows;

        return new NSScrollView
        {
            DocumentView = table,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            BorderType = NSBorderType.BezelBorder,
        };
    }
}

/// <summary>
/// What a popover's field does as it's typed in: says when it changes, and answers Return and Esc,
/// which would otherwise go to the field's own idea of them.
/// </summary>
/// <param name="changed">What's been typed has changed</param>
/// <param name="confirmed">Return</param>
/// <param name="cancelled">Esc</param>
internal sealed class FieldKeys(Action changed, Action confirmed, Action cancelled) : NSTextFieldDelegate
{
    /// <summary>↑ and ↓, moving through a list with the caret left in the field.</summary>
    public Action<int>? Step { get; init; }

    /// <summary>⌫, which goes on to the field unless this says it dealt with it.</summary>
    public Func<bool>? Backspace { get; init; }

    public override void Changed(NSNotification notification) => changed();

    public override bool DoCommandBySelector(NSControl control, NSTextView textView, Selector commandSelector)
    {
        switch (commandSelector.Name)
        {
            case "insertNewline:":
                confirmed();
                return true;

            case "cancelOperation:":
                cancelled();
                return true;

            case "moveDown:" when Step is { } step:
                step(1);
                return true;

            case "moveUp:" when Step is { } step:
                step(-1);
                return true;

            case "deleteBackward:" when Backspace is { } backspace:
                return backspace();

            default:
                return false;
        }
    }
}
