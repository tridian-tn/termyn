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
    /// The grid the picture's drawn on, in steps. The front box runs from 0 to 10 across and 4 to 14
    /// down, so its middle falls on a step and the plus and minus land on whole pixels at 100%.
    /// </summary>
    private const float Grid = 15f;

    /// <summary>
    /// Draws the pair of boxes, centred in <paramref name="bounds"/>.
    /// </summary>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The room the picture has, which it scales to</param>
    /// <param name="collapse">True for Collapse all's minus; false for Expand all's plus</param>
    /// <param name="colour">What to draw it in</param>
    internal static void Draw(Graphics g, Rectangle bounds, bool collapse, Color colour)
    {
        // Two thirds of the room: sixteen pixels in a 24-pixel button at 100%, which is what an
        // icon on a toolbar usually is.
        var size = Math.Max(10, Math.Min(bounds.Width, bounds.Height) * 2 / 3);
        var left = bounds.X + ((bounds.Width - size) / 2);
        var top = bounds.Y + ((bounds.Height - size) / 2);

        int X(float step) => left + (int)Math.Round(step * size / Grid);
        int Y(float step) => top + (int)Math.Round(step * size / Grid);

        var was = g.SmoothingMode;

        // All of it's horizontal and vertical lines, which smoothing only blurs.
        g.SmoothingMode = SmoothingMode.None;

        try
        {
            using var pen = new Pen(colour, Math.Max(1, (int)Math.Round(size / 16f)));

            // The box behind: only the part the front one doesn't cover, up and to the right.
            g.DrawLines(pen, [
                new Point(X(4), Y(4)),
                new Point(X(4), Y(0)),
                new Point(X(14), Y(0)),
                new Point(X(14), Y(10)),
                new Point(X(10), Y(10)),
            ]);

            g.DrawRectangle(pen, X(0), Y(4), X(10) - X(0), Y(14) - Y(4));

            // The minus, and for a plus the upright through it.
            g.DrawLine(pen, X(2.5f), Y(9), X(7.5f), Y(9));

            if (!collapse)
                g.DrawLine(pen, X(5), Y(6.5f), X(5), Y(11.5f));
        }
        finally
        {
            g.SmoothingMode = was;
        }
    }
}
