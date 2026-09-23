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
        if (Of(box) != this)
            SendMessage(box.Handle, EmSetZoom, Numerator, Denominator);
    }

    private const int EmGetZoom = 0x0400 + 224;
    private const int EmSetZoom = 0x0400 + 225;

    // DllImport rather than LibraryImport, matching the rest of the app.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, ref int wParam, ref int lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
