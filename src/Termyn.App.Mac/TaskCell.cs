using System.Globalization;
using ObjCRuntime;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// One task: its circle, its name, and a line under it saying when, where and with what labels.
/// </summary>
/// <remarks>
/// Colour on the second line is Todoist's, and only where it means something: an overdue date in
/// red, the project's dot, each label in its own colour. On a selected row all of it gives way to
/// the selection's text colour — the accent behind it is what the row is saying, and a second
/// colour over it says less, which is the same rule <see cref="OutlineCells"/> keeps on Windows.
///
/// The circle is a button, so clicking it ticks the task off without taking the selection with it,
/// the way the Windows box doesn't. The name is a label until it's renamed, when it becomes the
/// field it's edited in, where it stands — as Finder renames a file.
/// </remarks>
internal sealed class TaskCell : NSTableCellView
{
    private const int IndentPerLevel = 22;

    private readonly NSButton _circle = new() { Bordered = false, Title = string.Empty, ImagePosition = NSCellImagePosition.ImageOnly };
    private readonly NSTextField _title = NSTextField.CreateLabel(string.Empty);
    private readonly NSTextField _detail = NSTextField.CreateLabel(string.Empty);
    private readonly NSLayoutConstraint _indent;

    private TaskRow? _row;
    private IReadOnlyDictionary<string, Rgb> _labelColours = new Dictionary<string, Rgb>();
    private DateOnly _today;

    /// <summary>Where a rename that's open reports back to, or null when there's none.</summary>
    private Action<string?>? _renamed;

    public TaskCell(string identifier)
    {
        Identifier = identifier;

        _circle.SetButtonType(NSButtonType.MomentaryChange);
        _circle.Activated += (_, _) =>
        {
            if (_row is { } row)
                Toggle?.Invoke(row.Id);
        };

        _circle.SymbolConfiguration = NSImageSymbolConfiguration.Create(NSFontTextStyle.Body.GetConstant()!, NSImageSymbolScale.Large);
        _circle.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);

        _title.Font = Font(NSFontTextStyle.Body);
        _title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        _detail.Font = Font(NSFontTextStyle.Subheadline);
        _detail.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _detail.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var words = NSStackView.FromViews([_title, _detail]);
        words.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        words.Alignment = NSLayoutAttribute.Leading;
        words.Spacing = 2;

        foreach (var view in new NSView[] { _circle, words })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(view);
        }

        TextField = _title;
        _indent = _circle.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 4);

        NSLayoutConstraint.ActivateConstraints(
        [
            _indent,
            _circle.CenterYAnchor.ConstraintEqualTo(_title.CenterYAnchor),
            words.LeadingAnchor.ConstraintEqualTo(_circle.TrailingAnchor, 8),
            words.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -8),
            words.TopAnchor.ConstraintEqualTo(TopAnchor, 6),
            words.BottomAnchor.ConstraintEqualTo(BottomAnchor, -6),
        ]);
    }

    /// <summary>A day's heading in Upcoming, as a floating group row.</summary>
    public static NSTableCellView Day(string identifier)
    {
        var cell = new NSTableCellView { Identifier = identifier };
        var text = NSTextField.CreateLabel(string.Empty);
        text.Font = Font(NSFontTextStyle.Headline);
        text.TranslatesAutoresizingMaskIntoConstraints = false;
        cell.AddSubview(text);
        cell.TextField = text;

        NSLayoutConstraint.ActivateConstraints(
        [
            text.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 4),
            text.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.TrailingAnchor, -4),
            text.TopAnchor.ConstraintEqualTo(cell.TopAnchor, 8),
            text.BottomAnchor.ConstraintEqualTo(cell.BottomAnchor, -4),
        ]);
        return cell;
    }

    /// <summary>What clicking the circle does, given the task's id.</summary>
    public Action<string>? Toggle { get; set; }

    public void Show(TaskRow row, IReadOnlyDictionary<string, Rgb> labelColours, DateOnly today)
    {
        _row = row;
        _labelColours = labelColours;
        _today = today;
        _indent.Constant = 4 + (IndentPerLevel * row.Depth);
        Draw();
    }

    /// <summary>Redrawn when the row is selected or let go, so the colours give way to the selection.</summary>
    public override NSBackgroundStyle BackgroundStyle
    {
        get => base.BackgroundStyle;
        set
        {
            base.BackgroundStyle = value;
            Draw();
        }
    }

    private bool Selected => BackgroundStyle == NSBackgroundStyle.Emphasized;

    private void Draw()
    {
        if (_row is not { } row)
            return;

        var done = OutlineCells.DrawnDone(row);

        // What VoiceOver says for the circle is the symbol's own description: done, or the
        // priority, in the words the Priority menu uses. A task with no priority says nothing.
        _circle.Image = NSImage.GetSystemSymbol(
            done ? "checkmark.circle.fill" : "circle",
            done ? Strings.Completed : OutlineCells.PriorityTip(row));
        _circle.ContentTintColor = done ? NSColor.TertiaryLabel : Palette.Priority(row.Priority);
        _circle.ToolTip = done ? Strings.MarkIncomplete : Strings.MarkCompleted;

        _title.StringValue = row.Content;
        _title.TextColor = done ? NSColor.TertiaryLabel : NSColor.Label;
        _title.ToolTip = row.Content;

        var detail = Detail(row, done);
        _detail.AttributedStringValue = detail;
        _detail.Hidden = detail.Length == 0;
    }

    /// <summary>
    /// Opens the name for editing where it stands, with all of it selected.
    /// </summary>
    /// <param name="renamed">
    /// Told once, when the editing ends: the new name on Return or a click elsewhere, or null on Esc
    /// </param>
    public void BeginRename(Action<string?> renamed)
    {
        if (_row is null || Window is null)
            return;

        _renamed = renamed;
        _title.Editable = true;
        _title.Selectable = true;
        _title.Delegate = new Renaming(this);
        Window.MakeFirstResponder(_title);
        _title.CurrentEditor?.SelectAll(this);
    }

    /// <summary>Puts the name back to a label and says how the editing ended.</summary>
    private void EndRename(string? text)
    {
        if (_renamed is not { } renamed)
            return;

        // Let go before anything else, so ending the editing below doesn't come back round here.
        _renamed = null;
        _title.Delegate = null!;
        _title.AbortEditing();
        _title.Editable = false;
        _title.Selectable = false;
        Draw();

        renamed(text);
    }

    /// <summary>
    /// The name's field while it's being renamed: Return keeps the new name, Esc keeps the old one,
    /// and clicking away keeps the new one, as Finder does.
    /// </summary>
    private sealed class Renaming(TaskCell cell) : NSTextFieldDelegate
    {
        public override bool DoCommandBySelector(NSControl control, NSTextView textView, Selector commandSelector)
        {
            switch (commandSelector.Name)
            {
                case "cancelOperation:":
                    cell.EndRename(null);
                    return true;

                case "insertNewline:":
                    cell.EndRename(textView.Value);
                    return true;

                default:
                    return false;
            }
        }

        public override void EditingEnded(NSNotification notification) => cell.EndRename(cell._title.StringValue);
    }

    /// <summary>The line under the name: when, which project, which labels, and any conversation.</summary>
    private NSAttributedString Detail(TaskRow row, bool done)
    {
        var line = new NSMutableAttributedString();

        // A semantic colour rather than none: on a selected row the system draws it in the
        // selection's own text colour, which a run with no colour of its own wouldn't get.
        var quiet = NSColor.SecondaryLabel;

        void Add(string text, NSColor? colour)
        {
            if (line.Length > 0)
                line.Append(Run("   ", null));
            line.Append(Run(text, colour));
        }

        if (OutlineCells.CellOf(row, TaskColumn.Due) is { Length: > 0 } due)
        {
            var overdue = !Selected && !done && row.DueOn is { } on && on < _today;
            Add(due, overdue ? NSColor.SystemRed : quiet);
        }

        if (row.Project.Length > 0)
        {
            if (line.Length > 0)
                line.Append(Run("   ", null));
            if (OutlineCells.ProjectDot(row, Selected) is { } dot)
                line.Append(Run("● ", Palette.Of(dot)));
            line.Append(Run(row.Project, quiet));
        }

        foreach (var (text, colour) in OutlineCells.LabelRuns(row, Selected, _labelColours))
            Add(text, colour is { } c ? Palette.Of(c) : quiet);

        if (row.CommentCount > 0)
        {
            var bubble = new NSTextAttachment { Image = NSImage.GetSystemSymbol("bubble.left", string.Format(CultureInfo.CurrentCulture, Strings.CommentCount, row.CommentCount)) };
            if (line.Length > 0)
                line.Append(Run("   ", null));
            line.Append(NSAttributedString.FromAttachment(bubble));
            line.Append(Run(" " + row.CommentCount.ToString(CultureInfo.CurrentCulture), quiet));
        }

        return line;
    }

    /// <summary>
    /// The system's font for a text style, so the list follows the text size set in System
    /// Settings as Mail and Reminders do.
    /// </summary>
    private static NSFont Font(NSFontTextStyle style) => NSFont.GetPreferredFont(style.GetConstant()!, new NSDictionary());

    private NSAttributedString Run(string text, NSColor? colour)
    {
        var attributes = new NSStringAttributes { Font = _detail.Font };
        if (colour is not null)
            attributes.ForegroundColor = colour;

        return new NSAttributedString(text, attributes);
    }
}
