using ObjCRuntime;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// Typing into a popover's field, through the field's own delegate, which is where AppKit sends what
/// a key press means.
/// </summary>
internal static class PopoverKeys
{
    public static void Type(NSTextField field, string typed)
    {
        field.StringValue = typed;
        ((NSTextFieldDelegate)field.Delegate!).Changed(NSNotification.FromName(NSTextField.TextDidChangeNotification, field));
    }

    public static void Return(NSTextField field, string typed)
    {
        Type(field, typed);
        Press(field, "insertNewline:");
    }

    public static void Press(NSTextField field, string selector)
    {
        using var editor = new NSTextView();
        ((NSTextFieldDelegate)field.Delegate!).DoCommandBySelector(field, editor, new Selector(selector));
    }
}
