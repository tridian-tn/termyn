using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

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

    /// <summary>WM_MOUSEMOVE.</summary>
    private const int WmMouseMove = 0x0200;

    [Fact]
    public void The_pointer_crossing_a_row_redraws_it_through_a_paint()
    {
        // The style above is not enough on its own, which is the whole of why the outline still
        // flickered after it was set: the list redraws a row the first time the pointer enters it
        // and draws that one straight onto the window, where buffering only covers a paint. The
        // row's draws then land one at a time and it is watched being assembled.
        //
        // Both readings are asserted. That the row was asked for outside a paint says the fault
        // this guards against actually happened here — without it the test would pass on a machine
        // that never triggers it and prove nothing. That something was drawn inside a paint says
        // the row still gets drawn, which refusing to draw at all would also satisfy the first.
        using var form = new Form { Width = 900, Height = 600 };
        var outline = new OutlineView { Theme = Theme.Resolve(ThemePreference.Light), Dock = DockStyle.Fill };
        form.Controls.Add(outline);
        form.Show();

        outline.Rows = Enumerable.Range(0, 20)
            .Select(i => new TaskRow($"t{i}", $"task {i}", Priority.P4, "Work", string.Empty, []))
            .ToList();

        form.Refresh();
        Application.DoEvents();
        outline.ForgetDrawCounts();

        // Down the rows the way a hand crosses them, aimed at where the rows actually are rather
        // than at a guess: how tall one is follows from the font and the display, so counting in
        // pixels from the top would walk past them entirely on a machine unlike this one.
        for (var i = 0; i < 12; i++)
        {
            var row = outline.GetItemRect(i);
            var point = ((row.Top + row.Height / 2) << 16) | ((row.Left + 8) & 0xFFFF);

            SendMessage(outline.Handle, WmMouseMove, 0, point);
            Application.DoEvents();
        }

        Assert.True(outline.AskedOutsidePaint > 0, "the list never redrew a row outside a paint, so this proves nothing");
        Assert.True(outline.DrawnInPaint > 0, $"nothing was drawn through a paint: {outline.AskedOutsidePaint} asked outside");
    }
}
