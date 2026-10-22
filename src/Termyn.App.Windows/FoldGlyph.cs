using System.Drawing.Drawing2D;

namespace Termyn.App.Windows;

/// <summary>
/// The pictures on the Expand all and Collapse all buttons: two of the outline's own expanders, one
/// above the other.
/// </summary>
/// <remarks>
/// The same filled triangles a row's expander is drawn with, pointing down for open and right for
/// folded, so the buttons read as that expander done to every row at once. Two of them stacked are
/// what make it every row rather than one. Drawn rather than written for the expander's reasons: a
/// glyph would mean a font that has it, and three points apiece are cheaper.
/// </remarks>
internal static class FoldGlyph
{
    /// <summary>
    /// Draws the pair of expanders, centred in <paramref name="bounds"/>.
    /// </summary>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The room the picture has, which it scales to</param>
    /// <param name="collapsed">True for the folded pair, pointing right; false for the open one, pointing down</param>
    /// <param name="colour">What to draw it in</param>
    internal static void Draw(Graphics g, Rectangle bounds, bool collapsed, Color colour)
    {
        // A third of the room each, with a twelfth between them: at 100% that's eight pixels, the
        // same size as the expander on a row, and two of them still leave a margin in the button.
        var side = Math.Min(bounds.Width, bounds.Height);
        var arrow = Math.Max(4, side / 3);
        var gap = Math.Max(1, side / 12);

        var left = bounds.X + ((bounds.Width - arrow) / 2);
        var top = bounds.Y + ((bounds.Height - ((arrow * 2) + gap)) / 2);

        // Smoothed, since the diagonals are the whole shape and unsmoothed they read as a staircase.
        var was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            using var brush = new SolidBrush(colour);
            g.FillPolygon(brush, Arrow(left, top, arrow, collapsed));
            g.FillPolygon(brush, Arrow(left, top + arrow + gap, arrow, collapsed));
        }
        finally
        {
            g.SmoothingMode = was;
        }
    }

    /// <summary>One expander's triangle, the way the outline draws it.</summary>
    /// <param name="left">Its left edge</param>
    /// <param name="top">Its top edge</param>
    /// <param name="size">How wide and tall it is</param>
    /// <param name="collapsed">True to point right, false to point down</param>
    /// <returns>The triangle's three corners</returns>
    private static Point[] Arrow(int left, int top, int size, bool collapsed) => collapsed
        ? [new Point(left, top), new Point(left + size, top + (size / 2)), new Point(left, top + size)]
        : [new Point(left, top), new Point(left + size, top), new Point(left + (size / 2), top + size)];
}
