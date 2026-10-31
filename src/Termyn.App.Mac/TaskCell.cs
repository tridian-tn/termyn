using System.Globalization;
using ObjCRuntime;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// One task: its circle, its name, and a line under it saying when, where and with what labels —
/// or, in the table, its circle and name on one line, with the rest in columns of their own.
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
///
/// A task with sub-tasks has a disclosure triangle in front of its circle, in the list and the
/// table both: a task folded in one would otherwise be folded out of reach in the other. The room
/// for the triangle is kept on every row, so the circles line up whether a task has sub-tasks or not,
/// as the Windows outline keeps its expander's gutter.
/// </remarks>
internal sealed class TaskCell : NSTableCellView
{
    private const int IndentPerLevel = 22;

    /// <summary>Less in the table, whose rows are a line apiece and whose column is narrower.</summary>
    private const int CompactIndentPerLevel = 16;

    private const int DisclosureWidth = 16;

    private readonly NSButton _disclosure = new() { Title = string.Empty, BezelStyle = NSBezelStyle.Disclosure };

    private readonly NSButton _circle = new() { Bordered = false, Title = string.Empty, ImagePosition = NSCellImagePosition.ImageOnly };
    private readonly NSTextField _title = NSTextField.CreateLabel(string.Empty);
    private readonly NSTextField _detail = NSTextField.CreateLabel(string.Empty);
    private readonly NSLayoutConstraint _indent;
    private readonly bool _compact;

    private TaskRow? _row;
    private IReadOnlyDictionary<string, Rgb> _labelColours = new Dictionary<string, Rgb>();
    private DateOnly _today;

    /// <summary>Where a rename that's open reports back to, or null when there's none.</summary>
    private Action<string?>? _renamed;

    /// <param name="identifier">What the table reuses it by</param>
    /// <param name="compact">One line, for the table's Task column, rather than two</param>
    public TaskCell(string identifier, bool compact = false)
    {
        Identifier = identifier;
        _compact = compact;

        // On is open, which is the way the triangle points down.
        _disclosure.SetButtonType(NSButtonType.PushOnPushOff);
        _disclosure.Activated += (_, _) =>
        {
            if (_row is { } row)
                Fold?.Invoke(row.Id, _disclosure.State == NSCellStateValue.Off);
        };

        _circle.SetButtonType(NSButtonType.MomentaryChange);
        _circle.Activated += (_, _) =>
        {
            if (_row is { } row)
                Toggle?.Invoke(row.Id);
        };

        _circle.SymbolConfiguration = NSImageSymbolConfiguration.Create(
            NSFontTextStyle.Body.GetConstant()!,
            compact ? NSImageSymbolScale.Medium : NSImageSymbolScale.Large);
        _circle.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);

        _title.Font = Font(NSFontTextStyle.Body);
        _title.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        _detail.Font = Font(NSFontTextStyle.Subheadline);
        _detail.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _detail.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        // Side by side in the table, where the second line carries only the conversation's mark.
        var words = NSStackView.FromViews([_title, _detail]);
        words.Orientation = compact ? NSUserInterfaceLayoutOrientation.Horizontal : NSUserInterfaceLayoutOrientation.Vertical;
        words.Alignment = compact ? NSLayoutAttribute.FirstBaseline : NSLayoutAttribute.Leading;
        words.Spacing = compact ? 6 : 2;

        foreach (var view in new NSView[] { _disclosure, _circle, words })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(view);
        }

        TextField = _title;
        _indent = _disclosure.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 4);

        NSLayoutConstraint.ActivateConstraints(
        [
            _indent,
            _disclosure.WidthAnchor.ConstraintEqualTo(DisclosureWidth),
            _disclosure.CenterYAnchor.ConstraintEqualTo(_circle.CenterYAnchor),
            _circle.LeadingAnchor.ConstraintEqualTo(_disclosure.TrailingAnchor, 2),
            _circle.CenterYAnchor.ConstraintEqualTo(_title.CenterYAnchor),
            words.LeadingAnchor.ConstraintEqualTo(_circle.TrailingAnchor, compact ? 6 : 8),
            words.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -8),
        ]);

        // The table's rows are a set height, which the words sit in the middle of; the list's grow
        // to fit them.
        NSLayoutConstraint.ActivateConstraints(compact
            ? [words.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)]
            : [words.TopAnchor.ConstraintEqualTo(TopAnchor, 6), words.BottomAnchor.ConstraintEqualTo(BottomAnchor, -6)]);
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

    /// <summary>What clicking the triangle does, given the task's id and whether to fold it away.</summary>
    public Action<string, bool>? Fold { get; set; }

    public void Show(TaskRow row, IReadOnlyDictionary<string, Rgb> labelColours, DateOnly today)
    {
        _row = row;
        _labelColours = labelColours;
        _today = today;
        _indent.Constant = 4 + ((_compact ? CompactIndentPerLevel : IndentPerLevel) * row.Depth);
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

        _disclosure.Hidden = !row.HasChildren;
        _disclosure.State = row.Collapsed ? NSCellStateValue.Off : NSCellStateValue.On;
        _disclosure.ToolTip = row.Collapsed ? Strings.ShowSubtasks : Strings.HideSubtasks;

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

    /// <summary>
    /// The line under the name: when, which project, which labels, and any conversation. In the table
    /// the rest have columns of their own, and only the conversation's mark is left to go here.
    /// </summary>
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

        if (!_compact && OutlineCells.CellOf(row, TaskColumn.Due) is { Length: > 0 } due)
        {
            var overdue = !Selected && !done && row.DueOn is { } on && on < _today;
            Add(due, overdue ? NSColor.SystemRed : quiet);
        }

        if (!_compact && row.Project.Length > 0)
        {
            if (line.Length > 0)
                line.Append(Run("   ", null));
            if (OutlineCells.ProjectDot(row, Selected) is { } dot)
                line.Append(Run("● ", Palette.Of(dot)));
            line.Append(Run(row.Project, quiet));
        }

        if (!_compact)
        {
            foreach (var (text, colour) in OutlineCells.LabelRuns(row, Selected, _labelColours))
                Add(text, colour is { } c ? Palette.Of(c) : quiet);
        }

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
