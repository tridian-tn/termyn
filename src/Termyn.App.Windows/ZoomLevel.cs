using System.Runtime.InteropServices;

namespace Termyn.App.Windows;

/// <summary>
/// How far a rich edit control is scaled, as the ratio the control itself keeps.
/// </summary>
/// <remarks>
/// Handing the control a whole new document puts it back to its own size, and both halves of the
/// description panel load one every time they draw — on every task switch, sync and pause in the
/// typing — so a panel the user had scaled went back to its own size under them. Anything that
/// replaces the document takes one of these first and hands it back afterwards.
///
/// Read and set with the control's own messages rather than through
/// <see cref="RichTextBox.ZoomFactor"/>. That property remembers the scale it last set and skips a
/// set that matches it, and nothing tells it when a new document resets the control — so setting
/// back the very scale it was given does nothing at all. Kept as the ratio rather than a float, too,
/// so a scale the wheel chose goes back as exactly what it was.
/// </remarks>
/// <param name="Numerator">The top of the ratio, or nought when the control is at its own size</param>
/// <param name="Denominator">The bottom of the ratio, or nought when the control is at its own size</param>
internal readonly record struct ZoomLevel(int Numerator, int Denominator)
{
    /// <summary>What a control is scaled to now.</summary>
    /// <param name="box">The control to ask, which has to have a handle</param>
    /// <returns>Its scale</returns>
    internal static ZoomLevel Of(RichTextBox box)
    {
        var numerator = 0;
        var denominator = 0;
        SendMessage(box.Handle, EmGetZoom, ref numerator, ref denominator);
        return new ZoomLevel(numerator, denominator);
    }

    /// <summary>
    /// Scales a control to this, unless it's there already.
    /// </summary>
    /// <remarks>
    /// Left alone when it matches, because setting a scale lays the whole document out again — and a
    /// panel nobody has scaled, which is nearly all of them, would pay for that on every redraw.
    /// </remarks>
    /// <param name="box">The control to scale, which has to have a handle</param>
    internal void ApplyTo(RichTextBox box)
    {
        if (Of(box) == this)
            return;

        SendMessage(box.Handle, EmSetZoom, Numerator, Denominator);

        // Read back through the property, which is the only way it learns what it's missed. Left
        // behind, the next set through it that matched its stale memory would be skipped.
        _ = box.ZoomFactor;
    }

    /// <summary>
    /// Whether a message is the wheel with Ctrl held, which a rich edit control answers by scaling
    /// itself without going through its zoom property.
    /// </summary>
    /// <remarks>
    /// Read off the message rather than the keyboard: the control goes by the flag the message
    /// carries, so this does too.
    /// </remarks>
    /// <param name="m">The message the control has just handled</param>
    /// <returns>True when the control will have scaled itself</returns>
    internal static bool IsZoomWheel(in Message m)
        => m.Msg == WmMouseWheel && ((int)(long)m.WParam & MkControl) != 0;

    /// <summary>
    /// The control's font at the scale it's drawn at, for anything painted over it by hand.
    /// </summary>
    /// <param name="box">The control whose font and scale to use</param>
    /// <returns>A new font, which the caller disposes</returns>
    internal static Font ScaledFont(RichTextBox box)
        => new(box.Font.FontFamily, box.Font.Size * box.ZoomFactor, box.Font.Style, box.Font.Unit);

    private const int EmGetZoom = 0x0400 + 224;
    private const int EmSetZoom = 0x0400 + 225;
    private const int WmMouseWheel = 0x020A;
    private const int MkControl = 0x0008;

    // DllImport rather than LibraryImport, matching the rest of the app.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, ref int wParam, ref int lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
