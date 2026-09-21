using System.Drawing.Drawing2D;

namespace Termyn.App.Windows;

/// <summary>
/// The little calendar drawn on the button that opens a date picker.
/// </summary>
/// <remarks>
/// Drawn rather than written. A glyph would mean a font that has it, which is a thing to check for
/// and fall back from, and a handful of lines is cheaper than either — the same reasoning the
/// outline's expander is drawn under.
/// </remarks>
internal static class CalendarGlyph
{
    /// <summary>How thick the page is outlined.</summary>
    private const int Stroke = 1;

    /// <summary>How wide the rings above the page are drawn, and how tall they stand.</summary>
    private const int Ring = 2;

    /// <summary>
    /// Draws a calendar inside <paramref name="bounds"/>: a page, a band across the top with two
    /// rings above it, and two marks where the days would be.
    /// </summary>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The square the calendar fills</param>
    /// <param name="colour">What to draw it in</param>
    internal static void Draw(Graphics g, Rectangle bounds, Color colour)
    {
        // Under about ten pixels the band and the marks land on the same row and it reads as a
        // smudge, so nothing is drawn rather than something illegible.
        if (bounds.Width < 10 || bounds.Height < 10)
            return;

        var size = Math.Min(bounds.Width, bounds.Height);
        var page = new Rectangle(
            bounds.X + ((bounds.Width - size) / 2),
            bounds.Y + ((bounds.Height - size) / 2) + 2,
            size - 1,
            size - 3);

        var was = g.SmoothingMode;

        // The page and its marks are horizontal and vertical lines, which smoothing only blurs.
        g.SmoothingMode = SmoothingMode.None;

        try
        {
            using var pen = new Pen(colour, Stroke);
            using var brush = new SolidBrush(colour);

            g.DrawRectangle(pen, page);

            // The band across the top, where a real one carries the month.
            var band = page.Height / 4;
            g.FillRectangle(brush, page.X + 1, page.Y + 1, page.Width - 1, band);

            // The two rings it hangs by, standing above the page.
            var inset = Math.Max(Ring, page.Width / 6);
            g.FillRectangle(brush, page.X + inset, page.Y - Ring, Ring, Ring + 1);
            g.FillRectangle(brush, page.Right - inset - 1, page.Y - Ring, Ring, Ring + 1);

            // Two days on the page below it. One row rather than two: at sixteen pixels the rows
            // come out a pixel apart and read as a single smudged block.
            var day = new Size(Math.Max(3, page.Width / 4), Math.Max(3, page.Height / 4));
            var top = page.Y + band + ((page.Bottom - page.Y - band - day.Height) / 2);

            g.FillRectangle(brush, page.X + 2, top, day.Width, day.Height);
            g.FillRectangle(brush, page.X + 3 + day.Width, top, day.Width, day.Height);
        }
        finally
        {
            g.SmoothingMode = was;
        }
    }
}
