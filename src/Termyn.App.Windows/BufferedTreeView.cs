using System.Runtime.InteropServices;

namespace Termyn.App.Windows;

/// <summary>
/// A tree that paints itself off-screen first, so it isn't watched being drawn.
/// </summary>
/// <remarks>
/// A tree with full-row selection repaints a row as the pointer crosses it, and paints it straight
/// to the screen. <see cref="Control.DoubleBuffered"/> doesn't change that here, which is the part
/// worth writing down: for a list that flag reaches the common control's own buffering, and for a
/// tree it stops at the managed paint path — which is not the one doing the drawing. Setting it on a
/// tree leaves the extended style at nothing, and the flicker where it was.
///
/// So the switch is thrown directly, on the handle, and again on any handle after the first, since a
/// style belongs to the window it was set on.
/// </remarks>
internal sealed class BufferedTreeView : TreeView
{
    /// <summary>TVM_SETEXTENDEDSTYLE, which sets the bits named in wParam to the values in lParam.</summary>
    private const int TvmSetExtendedStyle = 0x1100 + 44;

    /// <summary>TVS_EX_DOUBLEBUFFER.</summary>
    internal const int TvsExDoubleBuffer = 0x0004;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(Handle, TvmSetExtendedStyle, TvsExDoubleBuffer, TvsExDoubleBuffer);
    }
}
