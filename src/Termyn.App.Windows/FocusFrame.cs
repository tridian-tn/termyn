using System.ComponentModel;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// A ring round the description, drawn in the accent while typing would change it.
/// </summary>
/// <remarks>
/// The markdown and the rendering look much alike for a description with little formatting in it, so
/// without this there's no telling at a glance whether a key pressed now would land in someone's
/// description. A text box showing that it has the focus is the ordinary thing; this is that, round
/// two controls that take turns.
///
/// The ring's room, and a little more, is held back by the frame's padding whether it's drawn or not,
/// so the text doesn't shift by a pixel when the focus arrives. Whoever holds the frame says when
/// typing would take: the markdown's showing and the account would keep what's typed.
///
/// The children's focus rather than the frame's Enter and Leave, which only fire for focus moving
/// within the window. Switching to another app takes the focus without either.
/// </remarks>
internal sealed class FocusFrame : Panel
{
    /// <summary>How thick the ring is at 100%.</summary>
    internal const int LogicalThickness = 1;

    /// <summary>
    /// How much of the panel's own colour is left between the ring and the text, at 100%.
    /// </summary>
    /// <remarks>
    /// The text boxes draw their first letter at their very edge, so with the ring straight against
    /// them it touched the first letter of every line.
    /// </remarks>
    internal const int LogicalGap = 3;

    private Theme _theme = Theme.Resolve(ThemePreference.System);
    private bool _takesTyping;

    /// <summary>How thick the ring is at the display's scale.</summary>
    private int _thickness = LogicalThickness;

    public FocusFrame()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(LogicalThickness + LogicalGap);
        SetStyle(ControlStyles.ResizeRedraw, true);
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
            BackColor = value.Panel;
            Invalidate();
        }
    }

    /// <summary>Whether typing here would change the description, which is when the ring is drawn.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool TakesTyping
    {
        get => _takesTyping;
        set
        {
            if (_takesTyping == value)
                return;

            _takesTyping = value;
            Invalidate();
        }
    }

    /// <summary>Whether the ring is drawn now, for a test to ask without reading pixels.</summary>
    internal bool Lit => _takesTyping && ContainsFocus;

    /// <summary>Sizes the ring for a display's scale.</summary>
    /// <param name="dpi">The display's dots per inch, which is 96 at 100%</param>
    public void ScaleTo(int dpi)
    {
        _thickness = StartupLayout.Scaled(LogicalThickness, dpi);
        Padding = new Padding(_thickness + StartupLayout.Scaled(LogicalGap, dpi));
        Invalidate();
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);

        if (e.Control is not { } child)
            return;

        child.GotFocus += OnChildFocus;
        child.LostFocus += OnChildFocus;
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        base.OnControlRemoved(e);

        if (e.Control is not { } child)
            return;

        child.GotFocus -= OnChildFocus;
        child.LostFocus -= OnChildFocus;
    }

    /// <summary>Draws the ring, or the panel's own colour where it would be.</summary>
    /// <param name="e">Where to draw</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (!Lit)
            return;

        using var brush = new SolidBrush(_theme.Accent);
        var outer = ClientRectangle;
        var inner = Rectangle.Inflate(outer, -_thickness, -_thickness);

        using var ring = new Region(outer);
        ring.Exclude(inner);
        e.Graphics.FillRegion(brush, ring);
    }

    private void OnChildFocus(object? sender, EventArgs e) => Invalidate();
}
