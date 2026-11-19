using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// That the outline draws every row of a paint that lands while it's being resized.
/// </summary>
/// <remarks>
/// WinForms skips a row that falls outside the size it has stored for the list, and leaves it to
/// the control's plain drawing instead: no box, no dots, the system's own highlight. It stores the
/// new size only once Windows has finished handling the resize, and Windows can paint the list
/// before then. Closing the description panel did exactly that: the list grew past its last row,
/// scrolled back up as part of the resize, and with smooth scrolling on that scroll paints as it
/// goes. A test window doesn't smooth-scroll, so the paint is asked for here instead, at the same
/// point in the resize.
/// </remarks>
public class OutlineResizePaintTests
{
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Bounds rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>NMHDR.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public nint From;
        public nint Id;
        public int Code;
    }

    /// <summary>The start of NMCUSTOMDRAW, as far as the item it's about.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CustomDraw
    {
        public Header Header;
        public int Stage;
        public nint Dc;
        public Bounds Area;
        public nint Item;
    }

    /// <summary>
    /// Paints the list on the spot each time it's told its new size, and counts the rows below the
    /// old edge that WinForms drew and the ones it handed back to the control.
    /// </summary>
    private sealed class MidResizePainter : NativeWindow
    {
        private const int WmSize = 0x0005;
        private const int WmReflectNotify = 0x2000 + 0x004E;
        private const int NmCustomDraw = -12;
        private const int CddsItemPrepaint = 0x00010001;
        private const int CdrfDoDefault = 0;

        private readonly OutlineView _outline;

        public MidResizePainter(OutlineView outline)
        {
            _outline = outline;
            AssignHandle(outline.Handle);
        }

        /// <summary>Where the list used to end; rows from here down are the ones uncovered.</summary>
        public int OldEdge { get; set; } = int.MaxValue;

        public bool Armed { get; set; }

        public int Paints { get; private set; }

        public int Drawn { get; private set; }

        public int HandedBack { get; private set; }

        protected override void WndProc(ref Message m)
        {
            if (Armed && m.Msg == WmReflectNotify && Marshal.PtrToStructure<CustomDraw>(m.LParam) is { Header.Code: NmCustomDraw, Stage: CddsItemPrepaint } draw)
            {
                base.WndProc(ref m);

                // Only rows the window really shows: the control is known to ask about others.
                var row = _outline.GetItemRect((int)draw.Item);
                GetClientRect(Handle, out var client);
                if (row.Top >= OldEdge && row.Top < client.Bottom)
                {
                    if (m.Result == CdrfDoDefault)
                        HandedBack++;
                    else
                        Drawn++;
                }

                return;
            }

            base.WndProc(ref m);

            if (Armed && m.Msg == WmSize)
            {
                const uint Invalidate = 0x0001, UpdateNow = 0x0100;
                Paints++;
                RedrawWindow(Handle, 0, 0, Invalidate | UpdateNow);
            }
        }
    }

    [WinFormsFact]
    public void A_paint_in_the_middle_of_a_resize_draws_the_uncovered_rows_itself()
    {
        // On a monitor, since a window that's on none is never painted at all.
        using var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = Screen.PrimaryScreen!.WorkingArea.Location,
            Size = new Size(900, 700),
            ShowInTaskbar = false,
        };
        var outline = new OutlineView { Theme = Theme.Resolve(ThemePreference.Light), Bounds = new Rectangle(0, 0, 800, 200) };
        form.Controls.Add(outline);
        form.Show();

        outline.Rows = Enumerable.Range(0, 60)
            .Select(i => new TaskRow($"t{i}", $"task {i}", Priority.P4, "Work", string.Empty, []))
            .ToList();

        form.Refresh();
        Application.DoEvents();

        var painter = new MidResizePainter(outline) { OldEdge = outline.ClientSize.Height, Armed = true };
        try
        {
            outline.Height = 600;
        }
        finally
        {
            painter.Armed = false;
            painter.ReleaseHandle();
        }

        // Without a paint mid-resize, or one that never reached the uncovered rows, nothing here
        // would be tested.
        Assert.True(painter.Paints > 0, "the list was never told its new size, so it was never painted mid-resize");
        Assert.True(painter.Drawn + painter.HandedBack > 0, "the paint never reached the rows the resize uncovered");
        Assert.Equal(0, painter.HandedBack);
    }
}
