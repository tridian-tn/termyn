using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// One of a task's cells in the table other than its name: the priority's flag, or the project,
/// due date, deadline or labels written out.
/// </summary>
/// <remarks>
/// The words are <see cref="OutlineCells"/>' own, so a column says here what it says on Windows.
/// Colour follows the same rule as the list's second line: Todoist's, only where it means
/// something, and none at all on a selected row, where the accent behind it is what the row says.
/// The rest of the text is the secondary label colour, as a Finder list writes its other columns.
/// </remarks>
internal sealed class GridCell : NSTableCellView
{
    /// <summary>The space a cell leaves either side of its words, in points.</summary>
    private const int Inset = 4;

    private readonly NSTextField _text = NSTextField.CreateLabel(string.Empty);
    private readonly NSImageView _flag = new();

    private TaskRow? _row;
    private TaskColumn _column;
    private IReadOnlyDictionary<string, Rgb> _labelColours = new Dictionary<string, Rgb>();
    private DateOnly _today;

    public GridCell(string identifier)
    {
        Identifier = identifier;

        _text.Font = TextFont;
        _text.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _flag.SymbolConfiguration = NSImageSymbolConfiguration.Create(NSFontTextStyle.Body.GetConstant()!, NSImageSymbolScale.Small);

        foreach (var view in new NSView[] { _text, _flag })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(view);
        }

        TextField = _text;
        ImageView = _flag;

        NSLayoutConstraint.ActivateConstraints(
        [
            _text.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, Inset),
            _text.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -Inset),
            _text.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _flag.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _flag.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
        ]);
    }

    /// <summary>The font a cell writes its words in, the system's body text.</summary>
    private static NSFont TextFont => NSFont.GetPreferredFont(NSFontTextStyle.Body.GetConstant()!, new NSDictionary());

    /// <summary>
    /// How wide a cell has to be to show some words without cutting them short.
    /// </summary>
    /// <remarks>
    /// Measured with a label like the cell's own, so the label's padding is counted as well as the
    /// space the cell leaves around it.
    /// </remarks>
    /// <param name="text">The words</param>
    /// <returns>The cell's width, in points</returns>
    public static double WidthFor(string text)
    {
        using var label = NSTextField.CreateLabel(text);
        label.Font = TextFont;

        double width = label.FittingSize.Width;
        return Math.Ceiling(width) + (Inset * 2);
    }

    public void Show(TaskRow row, TaskColumn column, IReadOnlyDictionary<string, Rgb> labelColours, DateOnly today)
    {
        _row = row;
        _column = column;
        _labelColours = labelColours;
        _today = today;
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
        var flagged = _column == TaskColumn.Priority && OutlineCells.PriorityTip(row) is not null;

        _flag.Hidden = !flagged;
        _text.Hidden = _column == TaskColumn.Priority;

        if (_column == TaskColumn.Priority)
        {
            var tip = OutlineCells.PriorityTip(row);
            _flag.Image = flagged ? NSImage.GetSystemSymbol("flag.fill", tip) : null;
            _flag.ContentTintColor = Selected ? NSColor.AlternateSelectedControlText : Palette.Priority(row.Priority);
            ToolTip = tip;
            return;
        }

        _text.AttributedStringValue = Words(row, done);
        ToolTip = _text.StringValue.Length > 0 ? _text.StringValue : null;
    }

    private NSAttributedString Words(TaskRow row, bool done)
    {
        // A finished task's cells fade with its name, as the list's second line does.
        var quiet = done ? NSColor.TertiaryLabel : NSColor.SecondaryLabel;
        var line = new NSMutableAttributedString();

        switch (_column)
        {
            case TaskColumn.Project when row.Project.Length > 0:
                if (OutlineCells.ProjectDot(row, Selected) is { } dot && !done)
                    line.Append(Palette.Dot(dot, _text.Font));
                line.Append(Run(row.Project, quiet));
                break;

            case TaskColumn.Labels:
                foreach (var (text, colour) in OutlineCells.LabelRuns(row, Selected, _labelColours))
                {
                    if (line.Length > 0)
                        line.Append(Run(" ", null));
                    line.Append(Run(text, colour is { } c && !done ? Palette.Of(c) : quiet));
                }

                break;

            case TaskColumn.Due:
                var overdue = !Selected && !done && row.DueOn is { } on && on < _today;
                line.Append(Run(OutlineCells.CellOf(row, TaskColumn.Due), overdue ? NSColor.SystemRed : quiet));
                break;

            default:
                line.Append(Run(OutlineCells.CellOf(row, _column), quiet));
                break;
        }

        return line;
    }

    private NSAttributedString Run(string text, NSColor? colour)
    {
        var attributes = new NSStringAttributes { Font = _text.Font };
        if (colour is not null)
            attributes.ForegroundColor = colour;

        return new NSAttributedString(text, attributes);
    }
}
