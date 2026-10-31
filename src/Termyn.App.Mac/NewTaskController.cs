using System.Globalization;
using ObjCRuntime;
using Termyn.Core.Logging;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The popover the toolbar's + opens, and ⌘N: a line to type a task into, in Todoist's quick-add
/// words, with what it'll make of them written underneath as you type.
/// </summary>
/// <remarks>
/// The same box as the Windows quick-add, driven the same way: filled in with the view's own
/// project or label where it has one, the caret put in front of that so what's typed reads first,
/// and what Todoist won't take from a quick-add said in place rather than lost.
/// </remarks>
internal sealed class NewTaskController(MainPresenter presenter, ILog log, Action close) : NSViewController
{
    private readonly NSTextField _field = new();
    private readonly NSTextField _preview = NSTextField.CreateWrappingLabel(string.Empty);

    /// <summary>What the box was filled in with, which on its own adds nothing.</summary>
    private string? _start;

    /// <summary>Set while a task is being added, so a second Return doesn't add it twice.</summary>
    private bool _adding;

    public override void LoadView()
    {
        _field.Delegate = new Typing(this);
        _field.WidthAnchor.ConstraintEqualTo(380).Active = true;

        _preview.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        _preview.PreferredMaxLayoutWidth = 380;

        var stack = NSStackView.FromViews([_field, _preview]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 6;
        stack.EdgeInsets = new NSEdgeInsets(14, 14, 14, 14);
        View = stack;
    }

    /// <summary>Fills the box in for the view on screen, ready to be shown.</summary>
    public void Prepare()
    {
        _ = View;
        _start = presenter.NewTaskText(presenter.Selection);
        _field.StringValue = _start is null ? string.Empty : " " + _start;
        _field.PlaceholderString = CapturePreviewText.Hint(presenter.CaptureReadsDays);
        ShowPreview();
    }

    /// <summary>Puts the caret at the start, ahead of whatever the view filled in.</summary>
    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        View.Window?.MakeFirstResponder(_field);
        if (_field.CurrentEditor is { } editor)
            editor.SelectedRange = new NSRange(0, 0);
    }

    /// <summary>Closes the popover, for the field's Esc.</summary>
    private void Finish() => close();

    private void ShowPreview()
    {
        _preview.StringValue = presenter.PreviewText(_field.StringValue);
        _preview.TextColor = NSColor.SecondaryLabel;
    }

    private void Problem(string text)
    {
        _preview.StringValue = text;
        _preview.TextColor = NSColor.SystemRed;
    }

    private async void Add()
    {
        var text = _field.StringValue;

        // Nothing typed, or only what the view filled in: closing is all there is to do.
        if (_adding)
            return;
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == _start?.Trim())
        {
            close();
            return;
        }

        _adding = true;
        try
        {
            await presenter.CaptureAsync(text, _start?.Trim());
            close();
        }
        catch (CaptureRefusedException refused)
        {
            // Not a failure: words a quick-add can't carry, said where they were typed.
            Problem(refused.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error("A task couldn't be added.", ex);
            Problem(string.Format(CultureInfo.CurrentCulture, Strings.ChangeFailed, ex.Message));
        }
        finally
        {
            _adding = false;
        }
    }

    private sealed class Typing(NewTaskController owner) : NSTextFieldDelegate
    {
        public override void Changed(NSNotification notification) => owner.ShowPreview();

        public override bool DoCommandBySelector(NSControl control, NSTextView textView, Selector commandSelector)
        {
            switch (commandSelector.Name)
            {
                case "insertNewline:":
                    owner.Add();
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
