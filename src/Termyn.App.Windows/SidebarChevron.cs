using System.Drawing.Drawing2D;

namespace Termyn.App.Windows;

/// <summary>
/// The small arrow after a sidebar heading that's been collapsed, so a closed group doesn't look
/// like an open one with nothing in it.
/// </summary>
/// <remarks>
/// Only a collapsed heading gets one. An open heading has its rows under it to say so, and an
/// empty one has nothing to open, so neither needs anything drawn. The headings are the tree's top
/// rows, and the tree has no expander for those: the sidebar turns them off so the headings sit at
/// the left edge.
///
/// Drawn rather than written, as the outline's expander is: a glyph would mean a font that has it.
/// </remarks>
internal static class SidebarChevron
{
    /// <summary>How wide the arrow is at 100%.</summary>
    internal const int Width = 4;

    /// <summary>How tall the arrow is at 100%.</summary>
    internal const int Height = 8;

    /// <summary>The space between the heading's last letter and the arrow, at 100%.</summary>
    internal const int Gap = 5;

    /// <summary>
    /// Works out where a heading's name and its arrow go.
    /// </summary>
    /// <remarks>
    /// The arrow follows the name rather than sitting at the sidebar's right edge, so it reads as
    /// part of the heading. When the sidebar's too narrow for both, the name gives way and the
    /// arrow stays: it's the one thing saying there's more under the heading.
    /// </remarks>
    /// <param name="bounds">The room the heading has</param>
    /// <param name="textWidth">How wide the name is when written in full</param>
    /// <param name="gap">The space before the arrow, at the display's scale</param>
    /// <param name="size">How big the arrow is, at the display's scale</param>
    /// <returns>The room left for the name, and the arrow's box</returns>
    internal static (Rectangle Text, Rectangle Arrow) Layout(Rectangle bounds, int textWidth, int gap, Size size)
    {
        var textRoom = Math.Max(0, Math.Min(textWidth, bounds.Width - gap - size.Width));
        var left = bounds.X + textRoom + gap;
        var top = bounds.Y + ((bounds.Height - size.Height) / 2);

        return (bounds with { Width = textRoom }, new Rectangle(left, top, size.Width, size.Height));
    }

    /// <summary>
    /// Draws the arrow, pointing right, in <paramref name="box"/>.
    /// </summary>
    /// <param name="g">What to draw on</param>
    /// <param name="box">Where the arrow goes</param>
    /// <param name="stroke">How thick its lines are</param>
    /// <param name="colour">What to draw it in</param>
    internal static void Draw(Graphics g, Rectangle box, int stroke, Color colour)
    {
        if (box.Width <= 0 || box.Height <= 0)
            return;

        // Smoothed, since it's two diagonals and unsmoothed at this size they read as a staircase.
        var was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            using var pen = new Pen(colour, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

            g.DrawLines(pen, [
                new Point(box.Left, box.Top),
                new Point(box.Right, box.Top + (box.Height / 2)),
                new Point(box.Left, box.Bottom),
            ]);
        }
        finally
        {
            g.SmoothingMode = was;
        }
    }
}
