using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The box a task's renamed in, and whether it opens over the name it's renaming.
/// </summary>
/// <remarks>
/// The list opens it where it would have written the name itself, at the left of the cell, but the
/// rows write their words past the indent, the expander and the box that ticks a task off. These
/// read where the box's letters actually land, by printing it, rather than trusting the arithmetic
/// that put it there.
/// </remarks>
public class OutlineRenameTests
{
    private const int LvmGetEditControl = 0x1000 + 24;
    private const int WmSetText = 0x000C;
    private const int WmPrint = 0x0317;
    private const int EmSetSel = 0x00B1;
    private const int PrfNonClient = 0x0002;
    private const int PrfClient = 0x0004;
    private const int PrfEraseBackground = 0x0008;

    private static TaskRow Task(string id, string content, int depth = 0)
        => new(id, content, Priority.P4, "Work", string.Empty, [], Depth: depth);

    private static Form Window(int width = 700)
        => new() { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(width, 400) };

    private static OutlineView Outline(Form form)
    {
        var outline = new OutlineView { Dock = DockStyle.Fill };
        form.Controls.Add(outline);
        form.Show();

        outline.Rows =
        [
            Task("a", "First"),
            Task("b", "Under it", depth: 1),
            Task("c", "Under that", depth: 2),
        ];

        return outline;
    }

    /// <summary>Opens a rename on a row, as F2 does, and hands back the box it's typed in.</summary>
    private static nint Rename(OutlineView outline, int index)
    {
        outline.Items[index].Selected = true;
        outline.Items[index].BeginEdit();
        Application.DoEvents();

        var box = SendMessage(outline.Handle, LvmGetEditControl, 0, 0);
        Assert.NotEqual(0, box);
        return box;
    }

    /// <summary>The box's edges, in the list's own coordinates.</summary>
    private static Rectangle BoundsOf(OutlineView outline, nint box)
    {
        GetWindowRect(box, out var screen);
        var topLeft = outline.PointToClient(new Point(screen.Left, screen.Top));
        return new Rectangle(topLeft, new Size(screen.Right - screen.Left, screen.Bottom - screen.Top));
    }

    /// <summary>
    /// Where the box's first letter lands, in the list's own coordinates.
    /// </summary>
    /// <remarks>
    /// The box opens with the whole name selected, which would paint it white on the highlight, so
    /// the selection's dropped first. The frame round the box is its outermost pixel, so the search
    /// starts inside it.
    /// </remarks>
    private static int FirstLetterIn(OutlineView outline, nint box)
    {
        SendMessage(box, EmSetSel, -1, 0);

        var bounds = BoundsOf(outline, box);
        using var printed = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(printed))
        {
            var dc = g.GetHdc();
            SendMessage(box, WmPrint, dc, PrfClient | PrfNonClient | PrfEraseBackground);
            g.ReleaseHdc(dc);
        }

        var inside = Rectangle.FromLTRB(1, 1, printed.Width - 1, printed.Height - 1);
        return bounds.X + FirstInk(printed, inside, printed.GetPixel(1, 1));
    }

    /// <summary>
    /// Where the row writes its first letter, in the list's own coordinates.
    /// </summary>
    /// <remarks>
    /// The list won't paint its rows into a bitmap, so the row's name is written into one the way
    /// the row writes it, with TextRenderer and the padding that leaves, and the letter found there.
    /// </remarks>
    private static int FirstLetterOnRow(OutlineView outline, int index)
    {
        var words = outline.WordsOf(index);
        using var written = new Bitmap(words.Width, words.Height);
        using (var g = Graphics.FromImage(written))
        {
            g.Clear(Color.White);
            TextRenderer.DrawText(
                g,
                outline.Rows[index].Content,
                outline.Font,
                new Rectangle(Point.Empty, words.Size),
                Color.Black,
                Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        return words.X + FirstInk(written, new Rectangle(Point.Empty, written.Size), Color.White);
    }

    /// <summary>The first column, from the left, with anything but the background in it.</summary>
    private static int FirstInk(Bitmap bitmap, Rectangle area, Color background)
    {
        for (var x = area.Left; x < area.Right; x++)
        {
            for (var y = area.Top; y < area.Bottom; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var difference = Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B);
                if (difference > 150) return x;
            }
        }

        throw new InvalidOperationException("There's nothing written there.");
    }

    /// <summary>The right-hand edge of the room that ticks a row's task off.</summary>
    private static int RightOfCheckbox(OutlineView outline, int index)
    {
        var row = outline.GetItemRect(index);
        var middle = row.Top + (row.Height / 2);
        var right = -1;

        for (var x = row.Left; x < row.Left + 200; x++)
        {
            if (outline.CheckboxAt(new Point(x, middle)) == outline.Rows[index].Id) right = x;
        }

        Assert.NotEqual(-1, right);
        return right;
    }

    [WinFormsTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_name_opens_where_the_row_writes_it(int index)
    {
        // It opened at the cell's left edge on every row, over the box and the expander, with the
        // name well to the left of where it had been written and further out the deeper the task.
        using var form = Window();
        using var outline = Outline(form);

        var box = Rename(outline, index);

        Assert.True(BoundsOf(outline, box).Left > RightOfCheckbox(outline, index), "The box covers the one that ticks the task off.");
        Assert.InRange(FirstLetterIn(outline, box) - FirstLetterOnRow(outline, index), -1, 1);
    }

    [WinFormsFact]
    public void The_name_stays_where_it_was_as_it_gets_longer()
    {
        // The list moves the box back to the cell's edge each time the name changes length, so a box
        // moved once, as it opened, would jump left at the first key.
        using var form = Window(width: 300);
        using var outline = Outline(form);

        // Starting with the row's own words, so the first letter's the same one the row writes.
        var box = Rename(outline, 1);
        SendMessage(box, WmSetText, 0, "Under it, and then long enough to run past the right-hand edge of the list");
        Application.DoEvents();

        Assert.InRange(FirstLetterIn(outline, box) - FirstLetterOnRow(outline, 1), -1, 1);
        Assert.True(BoundsOf(outline, box).Right <= outline.ClientSize.Width, "The box runs off the edge of the list.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
}
