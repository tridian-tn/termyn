using System.ComponentModel;

namespace Termyn.App.Windows;

/// <summary>
/// A single-line box that keeps its hint up for as long as it's empty, focused or not.
/// </summary>
/// <remarks>
/// WinForms draws <c>PlaceholderText</c> only while a box is unfocused, which is fine for one you
/// click into and no use at all for one that arrives focused. The quick-add popup is summoned
/// straight onto the caret, so its hint was never on screen for a moment — the box was doing
/// exactly what it was told and the answer was still nothing.
///
/// Drawn after the control has painted, so it sits on top of the background just laid down, the
/// same way the description editor draws its own.
/// </remarks>
internal sealed class HintTextBox : TextBox
{
    private const int WmPaint = 0x000F;

    /// <summary>What to show over an empty box.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Hint { get; set; } = string.Empty;

    /// <summary>What colour to draw it in, which is the theme's muted one.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HintColour { get; set; } = SystemColors.GrayText;

    /// <summary>
    /// Whether the hint is on show, which is whenever nothing has been typed. Deliberately says
    /// nothing about the focus: that it doesn't is the whole point of the control.
    /// </summary>
    public bool ShowingHint => TextLength == 0 && Hint.Length > 0;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmPaint || !ShowingHint)
            return;

        using var graphics = Graphics.FromHwnd(Handle);
        using var brush = new SolidBrush(HintColour);

        // Where the caret sits, so the hint reads as text about to be replaced rather than as a
        // label pasted into the corner.
        graphics.DrawString(Hint, Font, brush, 1, 1);
    }

    /// <summary>
    /// Repaints as the typing starts and again when the box is cleared, since the hint goes and
    /// comes back on those and the control has no reason of its own to know it.
    /// </summary>
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }
}
