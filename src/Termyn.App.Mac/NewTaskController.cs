using System.Globalization;
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
///
/// Opened on a task it adds a sub-task to it, saying which at the top. The words are still quick-add
/// words, so a priority or a day can be typed, but where it goes isn't up for discussion: a sub-task
/// lives in its parent's project, so there's no preview of where it's headed.
/// </remarks>
/// <param name="presenter">What the task is added through</param>
/// <param name="log">Where a failure is written</param>
/// <param name="close">Closes the popover</param>
/// <param name="parentId">The task to add a sub-task to, or null for a task of its own</param>
/// <param name="added">Told the new sub-task's id</param>
internal sealed class NewTaskController(
    MainPresenter presenter,
    ILog log,
    Action close,
    string? parentId = null,
    Action<string>? added = null) : NSViewController
{
    private readonly NSTextField _field = new();
    private readonly NSTextField _preview = NSTextField.CreateWrappingLabel(string.Empty);

    /// <summary>What the box was filled in with, which on its own adds nothing.</summary>
    private string? _start;

    /// <summary>Set while a task is being added, so a second Return doesn't add it twice.</summary>
    private bool _adding;

    public override void LoadView()
    {
        _field.Delegate = new FieldKeys(ShowPreview, Add, Finish);
        _field.WidthAnchor.ConstraintEqualTo(380).Active = true;

        _preview.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        _preview.PreferredMaxLayoutWidth = 380;

        var stack = NSStackView.FromViews(parentId is null ? [_field, _preview] : [Under(), _field, _preview]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 6;
        stack.EdgeInsets = new NSEdgeInsets(14, 14, 14, 14);
        View = stack;
    }

    /// <summary>The line saying which task a sub-task is going under.</summary>
    private NSTextField Under()
    {
        var parent = presenter.Rows.FirstOrDefault(r => r.Id == parentId && !r.IsHeading)?.Content ?? string.Empty;
        var under = NSTextField.CreateLabel(string.Format(CultureInfo.CurrentCulture, Strings.SubtaskUnder, parent));
        under.Font = NSFont.BoldSystemFontOfSize(NSFont.SystemFontSize)!;
        under.LineBreakMode = NSLineBreakMode.TruncatingTail;
        under.PreferredMaxLayoutWidth = 380;
        under.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        under.WidthAnchor.ConstraintLessThanOrEqualTo(380).Active = true;
        return under;
    }

    /// <summary>Fills the box in for the view on screen, ready to be shown.</summary>
    public void Prepare()
    {
        _ = View;

        if (parentId is not null)
        {
            _field.PlaceholderString = Strings.SubtaskPlaceholder;
            return;
        }

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
        if (parentId is not null)
            return;

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
            if (parentId is { } parent)
            {
                // Null when the parent has stopped being able to take one since the popover opened.
                // Left open, so the words aren't lost along with it.
                if (presenter.AddSubtask(parent, text) is not { } id)
                {
                    Problem(Strings.SubtaskRefused);
                    return;
                }

                added?.Invoke(id);
            }
            else
            {
                await presenter.CaptureAsync(text, _start?.Trim());
            }

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
}
