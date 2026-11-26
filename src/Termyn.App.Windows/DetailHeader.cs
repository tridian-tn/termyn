using System.ComponentModel;
using Termyn.Core.Settings;

namespace Termyn.App.Windows;

/// <summary>
/// The line across the top of the detail panel, naming what the panel is about.
/// </summary>
/// <remarks>
/// The tabs below say which side of a task you are looking at and never which task, and the outline
/// can be scrolled away from the row that is selected — so without this the panel is a description
/// and a conversation belonging to nothing you can see. Some clutter for an answer that was
/// otherwise only available by scrolling back.
///
/// A panel rather than a bare label so there is somewhere for the rest to go. The name fills what is
/// left after anything docked to the right of it, which is where read-only detail about the subject
/// — a due date, a count, whatever earns its room — can be hung without moving the name.
/// </remarks>
internal sealed class DetailHeader : Panel
{
    /// <summary>
    /// How tall the strip is at 100%.
    /// </summary>
    /// <remarks>
    /// Scaled with the display by whoever holds it, the way the line above the outline is. Held at
    /// 24 pixels whatever the scale, the name's tails were cut off from 125% up as the text grew and
    /// the strip didn't. The same height as that line now, which the larger name needs.
    /// </remarks>
    internal const int LogicalHeight = 28;

    /// <summary>How much bigger than the panel's own text the name is drawn.</summary>
    /// <remarks>
    /// A tenth bigger read as hardly bigger at all beside the tabs. Grow it further and the strip
    /// has to grow with it, or the name loses its tails at the larger display scales.
    /// </remarks>
    private const float NameScale = 1.25f;

    private readonly Label _name;
    private Theme _theme = Theme.Resolve(ThemePreference.System);

    /// <summary>The bold face the name is drawn in, built from the panel's font and kept to dispose.</summary>
    private Font? _nameFont;

    public DetailHeader()
    {
        Dock = DockStyle.Top;
        Height = LogicalHeight;

        // The bottom row kept back from whatever is docked inside, so the rule drawn there isn't
        // painted over by a child filling the panel.
        Padding = new Padding(0, 0, 0, 1);

        _name = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,

            // A task is called whatever its own text says, and some of them are sentences.
            AutoEllipsis = true,
            Padding = new Padding(6, 0, 6, 0),
            TextAlign = ContentAlignment.MiddleLeft,

            // The account's text, so "R&D" is a name and not an underlined D.
            UseMnemonic = false,
        };

        Controls.Add(_name);
        ApplyFont();
        ApplyTheme();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ApplyFont();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _nameFont?.Dispose();
    }

    /// <summary>
    /// Gives the name a bold face a step bigger than the panel's text.
    /// </summary>
    /// <remarks>
    /// It's what the whole panel is about, and drawn muted at body size it read as a caption to the
    /// tabs. Built afresh whenever the panel's font changes, since that's how a change of display
    /// scale arrives, and a font set on the label itself no longer follows its parent's.
    /// </remarks>
    private void ApplyFont()
    {
        var previous = _nameFont;

        _nameFont = new Font(Font.FontFamily, Font.SizeInPoints * NameScale, FontStyle.Bold, GraphicsUnit.Point);
        _name.Font = _nameFont;

        previous?.Dispose();
    }

    /// <summary>What the panel is about, or empty when the selection names nothing it can show.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Subject
    {
        get => _name.Text;
        set => _name.Text = value;
    }

    /// <summary>The colours to draw with.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            ApplyTheme();
        }
    }

    /// <summary>
    /// Draws the rule along the bottom.
    /// </summary>
    /// <remarks>
    /// Without it the name sits straight on the tab strip and reads as part of it. Drawn on the row
    /// the padding holds back, which is the only part of the panel a docked child doesn't cover.
    /// </remarks>
    /// <param name="e">Where to draw</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var pen = new Pen(_theme.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    /// <summary>Puts the current colours on, which is everything the header has of its own.</summary>
    private void ApplyTheme()
    {
        BackColor = _theme.Panel;
        _name.BackColor = _theme.Panel;
        _name.ForeColor = _theme.Text;
        Invalidate();
    }
}
