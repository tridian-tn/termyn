using ObjCRuntime;

namespace Termyn.App.Mac;

/// <summary>
/// A popover for picking from a list: what it's about in bold, a search field, and the list under
/// that, narrowed as you type.
/// </summary>
/// <remarks>
/// The keyboard stays in the field the whole time. The list refuses it, so a click on a row selects
/// the row and leaves the caret where it was, and the arrows move through the list from the field.
/// That keeps Return and Esc meaning the same thing however the list has been used: they're always
/// the field's to answer. Esc is answered further up as well, for the rare keyboard setting that
/// lets a checkbox or a button take the focus by Tab, where the popover would otherwise just close.
/// </remarks>
/// <param name="close">Closes the popover</param>
internal abstract class SearchPopoverController(Action close) : NSViewController
{
    protected NSSearchField Field { get; } = new();

    protected NSTableView Table { get; } = new();

    /// <summary>How many rows the list has at the moment.</summary>
    protected abstract int RowCount { get; }

    /// <summary>Narrows the list to what's been typed, and says which row it rests on.</summary>
    protected abstract void ShowMatches();

    /// <summary>What Return does.</summary>
    protected abstract void Confirm();

    /// <summary>Told when Esc is what's closing the popover, before it closes.</summary>
    protected virtual void Cancelling()
    {
    }

    protected void Close() => close();

    /// <summary>Lays the popover out, ready to be the view.</summary>
    /// <param name="heading">What the popover's about, usually the task's name</param>
    /// <param name="placeholder">What the search field says while it's empty</param>
    /// <param name="source">The list's rows</param>
    /// <param name="rows">The list's cells</param>
    /// <param name="width">How wide the popover is</param>
    /// <param name="listHeight">How tall the list is</param>
    /// <param name="button">A button to go under the list, or null for none</param>
    protected NSStackView Lay(
        string heading,
        string placeholder,
        NSTableViewDataSource source,
        NSTableViewDelegate rows,
        int width,
        int listHeight,
        NSButton? button = null)
    {
        const int Margin = 14;

        var name = NSTextField.CreateLabel(heading);
        name.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize)!;
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        Field.PlaceholderString = placeholder;
        Field.Delegate = new Typing(this);

        Table.AddColumn(new NSTableColumn("Row") { ResizingMask = NSTableColumnResizing.Autoresizing });
        Table.HeaderView = null;
        Table.Style = NSTableViewStyle.Plain;
        Table.ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly;
        Table.RefusesFirstResponder = true;
        Table.DataSource = source;
        Table.Delegate = rows;

        var scroll = new NSScrollView
        {
            DocumentView = Table,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            BorderType = NSBorderType.BezelBorder,
        };

        var stack = NSStackView.FromViews(button is null ? [name, Field, scroll] : [name, Field, scroll, button]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Trailing;
        stack.Spacing = 8;
        stack.EdgeInsets = new NSEdgeInsets(Margin, Margin, Margin, Margin);

        NSLayoutConstraint.ActivateConstraints(
        [
            stack.WidthAnchor.ConstraintEqualTo(width),
            name.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -2 * Margin),
            Field.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -2 * Margin),
            scroll.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -2 * Margin),
            scroll.HeightAnchor.ConstraintEqualTo(listHeight),
        ]);

        return stack;
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        View.Window?.MakeFirstResponder(Field);
    }

    /// <summary>Esc from anywhere the field isn't, which would otherwise close the popover unannounced.</summary>
    [Export("cancelOperation:")]
    private void CancelOperation(NSObject? sender) => Cancel();

    private void Cancel()
    {
        Cancelling();
        close();
    }

    private void Step(int by)
    {
        if (RowCount > 0)
        {
            var row = Math.Clamp((int)Table.SelectedRow + by, 0, RowCount - 1);
            Table.SelectRow(row, byExtendingSelection: false);
            Table.ScrollRowToVisible(row);
        }
    }

    private sealed class Typing(SearchPopoverController owner) : NSSearchFieldDelegate
    {
        public override void Changed(NSNotification notification) => owner.ShowMatches();

        public override bool DoCommandBySelector(NSControl control, NSTextView textView, Selector commandSelector)
        {
            switch (commandSelector.Name)
            {
                case "insertNewline:":
                    owner.Confirm();
                    return true;

                case "cancelOperation:":
                    owner.Cancel();
                    return true;

                case "moveDown:":
                    owner.Step(1);
                    return true;

                case "moveUp:":
                    owner.Step(-1);
                    return true;

                default:
                    return false;
            }
        }
    }
}
