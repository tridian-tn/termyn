using System.Drawing.Drawing2D;

namespace Termyn.App.Windows;

/// <summary>
/// The pictures on the Expand all and Collapse all buttons: a box with a plus or a minus on it, in
/// front of another box.
/// </summary>
/// <remarks>
/// The plus and minus are the old tree view's own marks for opening and closing a branch, and the
/// box behind is what makes it every branch rather than one — the pair the IDEs use for the same
/// two commands. Drawn rather than written, for the reasons the outline's expander is: a glyph would
/// mean a font that has it, and a few lines are cheaper.
/// </remarks>
internal static class FoldGlyph
{
    /// <summary>
    /// Where the picture's parts go in a button.
    /// </summary>
    /// <param name="Front">The front box, whose edges the outline is drawn along</param>
    /// <param name="Behind">The part of the box behind that the front one doesn't cover</param>
    /// <param name="Centre">The middle of the front box, where the plus and minus cross</param>
    /// <param name="Arm">How far the plus and minus reach either side of the middle</param>
    /// <param name="Stroke">How thick the lines are</param>
    internal readonly record struct Shape(Rectangle Front, Point[] Behind, Point Centre, int Arm, int Stroke);

    /// <summary>
    /// Works out where the picture goes in <paramref name="bounds"/>.
    /// </summary>
    /// <remarks>
    /// In whole pixels throughout, and the front box an even number of them across, so its middle is
    /// a pixel rather than the line between two and the plus sits square in it at any size. At 100%
    /// the picture's sixteen pixels in a 24-pixel button: a ten-pixel box with the other four up and
    /// to the right, and a plus three either side.
    /// </remarks>
    /// <param name="bounds">The room the picture has, which it scales to</param>
    /// <returns>Where each part goes</returns>
    internal static Shape Layout(Rectangle bounds)
    {
        // Two thirds of the room, which is what an icon on a toolbar usually takes.
        var size = Math.Max(10, Math.Min(bounds.Width, bounds.Height) * 2 / 3);
        var unit = size / 16f;

        int Steps(float steps, int least) => Math.Max(least, (int)Math.Round(steps * unit, MidpointRounding.AwayFromZero));

        var half = Steps(5, 3);
        var offset = Steps(4, 2);
        var arm = Steps(3, 1);
        var extent = (half * 2) + offset;

        var left = bounds.X + ((bounds.Width - extent) / 2);
        var top = bounds.Y + ((bounds.Height - extent) / 2) + offset;
        var front = new Rectangle(left, top, half * 2, half * 2);

        Point[] behind =
        [
            new(front.Left + offset, front.Top),
            new(front.Left + offset, front.Top - offset),
            new(front.Right + offset, front.Top - offset),
            new(front.Right + offset, front.Bottom - offset),
            new(front.Right, front.Bottom - offset),
        ];

        // Away from zero rather than to even, so the lines thicken in step with the scale: to even,
        // 250% came out as thin as 150%.
        var stroke = Math.Max(1, (int)Math.Round(size / 16f, MidpointRounding.AwayFromZero));

        return new Shape(front, behind, new Point(left + half, top + half), arm, stroke);
    }

    /// <summary>
    /// Draws the pair of boxes, centred in <paramref name="bounds"/>.
    /// </summary>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The room the picture has, which it scales to</param>
    /// <param name="collapse">True for Collapse all's minus; false for Expand all's plus</param>
    /// <param name="colour">What to draw it in</param>
    internal static void Draw(Graphics g, Rectangle bounds, bool collapse, Color colour)
    {
        var shape = Layout(bounds);
        var (x, y) = (shape.Centre.X, shape.Centre.Y);

        var was = g.SmoothingMode;

        // All of it's horizontal and vertical lines, which smoothing only blurs.
        g.SmoothingMode = SmoothingMode.None;

        try
        {
            using var pen = new Pen(colour, shape.Stroke);

            g.DrawLines(pen, shape.Behind);
            g.DrawRectangle(pen, shape.Front);

            // The minus, and for a plus the upright through it.
            g.DrawLine(pen, x - shape.Arm, y, x + shape.Arm, y);

            if (!collapse)
                g.DrawLine(pen, x, y - shape.Arm, x, y + shape.Arm);
        }
        finally
        {
            g.SmoothingMode = was;
        }
    }
}
