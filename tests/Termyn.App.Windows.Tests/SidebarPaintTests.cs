using System.Runtime.InteropServices;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// That the outline paints itself off-screen before it is shown.
/// </summary>
/// <remarks>
/// Its sibling, that the sidebar tree does the same, is deliberately not here. A tree only takes
/// that style from a process with visual styles enabled, which the app does on the way up and a test
/// host never does — so the assertion would read 0 whether the control asked for it or not, and pass
/// only by asking nothing. <see cref="BufferedTreeView"/> carries the reasoning instead; the switch
/// itself was checked by reading it off the running window.
/// </remarks>
public class SidebarPaintTests
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    /// <summary>LVM_GETEXTENDEDLISTVIEWSTYLE.</summary>
    private const int LvmGetExtendedListViewStyle = 0x1000 + 55;

    /// <summary>LVS_EX_DOUBLEBUFFER.</summary>
    private const int LvsExDoubleBuffer = 0x00010000;

    [Fact]
    public void The_outline_buffers_its_own_drawing()
    {
        // DoubleBuffered is what turns this on, which is worth holding down: on a list that flag
        // reaches the common control's own buffering, so it looks removable and is not. Taking it
        // off drops this style to nothing and the flicker comes back.
        using var outline = new OutlineView();
        outline.CreateControl();

        var styles = (int)SendMessage(outline.Handle, LvmGetExtendedListViewStyle, 0, 0);

        Assert.Equal(LvsExDoubleBuffer, styles & LvsExDoubleBuffer);
    }
}
