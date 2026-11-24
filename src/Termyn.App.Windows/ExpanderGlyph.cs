using System.Drawing.Drawing2D;

namespace Termyn.App.Windows;

/// <summary>
/// The arrow that hides and shows what's under a row — right when it's hidden, down when it isn't.
/// </summary>
/// <remarks>
/// One arrow for the outline's sub-tasks and the sidebar's groups and sub-projects, so the window
/// has one way of saying a row folds. The sidebar's tree would otherwise draw its own boxes for the
/// projects and nothing for the headings, which are its top rows.
/// </remarks>
internal static class ExpanderGlyph
{
    /// <summary>
    /// How much room the arrow takes at the head of a row, at 100%.
    /// </summary>
    /// <remarks>
    /// Kept on every row and not only the ones that have an arrow, so a row that folds and one that
    /// doesn't start their words in the same place. A gutter that appeared and vanished would
    /// shuffle the whole column sideways as rows gained and lost children.
    /// </remarks>
    internal const int RoomAt96 = 14;

    /// <summary>How big the arrow itself is drawn inside that room, at 100%.</summary>
    internal const int SizeAt96 = 8;

    /// <summary>
    /// Draws the arrow, centred in <paramref name="bounds"/>.
    /// </summary>
    /// <remarks>
    /// Drawn rather than written. A glyph would mean a font that has it, which is a thing to check
    /// for and fall back from, and three points are cheaper than either. Filled rather than outlined
    /// so it reads at this size in both themes.
    /// </remarks>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The room the arrow has</param>
    /// <param name="collapsed">Whether what's under the row is hidden</param>
    /// <param name="glyph">How big the arrow is at most, at the display's scale</param>
    /// <param name="colour">What to draw it in</param>
    internal static void Draw(Graphics g, Rectangle bounds, bool collapsed, int glyph, Color colour)
    {
        var size = Math.Min(glyph, Math.Min(bounds.Width, bounds.Height));
        var left = bounds.X + ((bounds.Width - size) / 2);
        var top = bounds.Y + ((bounds.Height - size) / 2);

        var arrow = collapsed
            ? new[] { new Point(left, top), new Point(left + size, top + (size / 2)), new Point(left, top + size) }
            : new[] { new Point(left, top), new Point(left + size, top), new Point(left + (size / 2), top + size) };

        // Smoothed, since the diagonals are the whole shape and unsmoothed they read as a staircase.
        var was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var brush = new SolidBrush(colour))
            g.FillPolygon(brush, arrow);

        g.SmoothingMode = was;
    }
}
