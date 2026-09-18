using System.Drawing.Drawing2D;

namespace Termyn.App.Windows;

/// <summary>
/// The small filled circles the window marks things with — a task's priority, a project's colour.
/// </summary>
/// <remarks>
/// Smoothed, which at this size is the whole of the difference between a circle and an octagon:
/// eight pixels across, an unsmoothed ellipse steps visibly at every edge. The mode is put back
/// afterwards, since the text drawn either side of a dot wants the default.
/// </remarks>
internal static class Dots
{
    /// <summary>
    /// Fills a circle inside <paramref name="bounds"/>, as round as the size allows.
    /// </summary>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The square the circle fills</param>
    /// <param name="colour">What to fill it with</param>
    internal static void Fill(Graphics g, Rectangle bounds, Color colour)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var was = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        try
        {
            using var brush = new SolidBrush(colour);
            g.FillEllipse(brush, bounds);
        }
        finally
        {
            g.SmoothingMode = was;
        }
    }
}
