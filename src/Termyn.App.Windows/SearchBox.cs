using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Termyn.App.Windows;

/// <summary>
/// A search box with something to click to empty it.
/// </summary>
/// <remarks>
/// The cross is a label sitting on the box rather than something painted onto it. The box is a
/// native control and drawing over one is how the task list came to flicker: a draw that arrives
/// outside a paint goes straight to the screen. A child control is painted by the same machinery
/// as everything else and needs none of that.
///
/// It shows only when there is something to clear, so an empty box is an empty box.
/// </remarks>
internal sealed class SearchBox : TextBox
{
    /// <summary>EM_SETMARGINS, and the flag for the right-hand one.</summary>
    private const int EmSetMargins = 0x00D3;

    private const int EcRightMargin = 0x0002;

    /// <summary>How much room the cross takes, in pixels.</summary>
    private const int ResetWidth = 20;

    private readonly Label _reset;

    public SearchBox()
    {
        _reset = new Label
        {
            Text = "✕",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            Visible = false,
        };

        _reset.Click += (_, _) => Reset();
        Controls.Add(_reset);
    }

    /// <summary>What colour to draw the cross in, which is the theme's muted one.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ResetColour
    {
        get => _reset.ForeColor;
        set => _reset.ForeColor = value;
    }

    /// <summary>Whether the cross is on show, which is whenever there is something to clear.</summary>
    public bool ShowingReset => _reset.Visible;

    /// <summary>
    /// Empties the box, as the cross does.
    /// </summary>
    /// <remarks>
    /// The focus goes to the box rather than staying on a cross that has just disappeared, since
    /// what someone does after clearing a search is usually type another one.
    /// </remarks>
    public void Reset()
    {
        Clear();

        if (CanFocus)
            Focus();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        _reset.Visible = TextLength > 0;
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);

        // Its own rather than transparent: a label asking to be transparent asks its parent to
        // paint behind it, and the parent here is a native edit control that won't.
        _reset.BackColor = BackColor;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _reset.BackColor = BackColor;
        PlaceReset();

        // Keeps the words off the cross. Without it a long search runs under it and the last of
        // what was typed is behind the thing offering to delete it.
        SendMessage(Handle, EmSetMargins, EcRightMargin, ResetWidth << 16);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        PlaceReset();
    }

    private void PlaceReset()
        => _reset.Bounds = new Rectangle(ClientSize.Width - ResetWidth, 0, ResetWidth, ClientSize.Height);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, int wParam, nint lParam);
}
