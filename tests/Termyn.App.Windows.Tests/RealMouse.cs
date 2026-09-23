using System.Runtime.InteropServices;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Clicks delivered as the messages a mouse sends, rather than by calling a control's handlers.
/// </summary>
/// <remarks>
/// A list does its own work on a press after the handlers have run — it selects the row under it —
/// so a test that called OnMouseDown would pass while a real click did something else. Both halves
/// are posted before either is pumped: a list takes the mouse on a press and waits inside its own
/// loop for the release, and one that isn't already queued never arrives.
/// </remarks>
internal static class RealMouse
{
    private const int WmLeftDown = 0x0201;
    private const int WmLeftUp = 0x0202;
    private const int WmLeftDouble = 0x0203;
    private const int LeftButton = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    /// <summary>A press and release of the left button.</summary>
    /// <param name="control">What's clicked</param>
    /// <param name="at">Where, in the control's own coordinates</param>
    public static void Click(Control control, Point at) => Press(control, WmLeftDown, at);

    /// <summary>The second press of a double-click, which Windows sends in place of a plain one.</summary>
    /// <param name="control">What's clicked</param>
    /// <param name="at">Where, in the control's own coordinates</param>
    public static void DoubleClick(Control control, Point at) => Press(control, WmLeftDouble, at);

    private static void Press(Control control, int down, Point at)
    {
        var position = (nint)((at.Y << 16) | (at.X & 0xFFFF));

        PostMessage(control.Handle, down, LeftButton, position);
        PostMessage(control.Handle, WmLeftUp, 0, position);

        // A few rounds, since what the press sets off can post messages of its own.
        for (var round = 0; round < 5; round++)
            Application.DoEvents();
    }
}
