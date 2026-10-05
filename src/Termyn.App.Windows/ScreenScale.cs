using System.Runtime.InteropServices;

namespace Termyn.App.Windows;

/// <summary>
/// The scale a screen runs at, which a window only learns for itself once it's on one.
/// </summary>
/// <remarks>
/// Before its handle exists a window reports the scale of the screen the app started on. Sized by
/// that and then opened on a second screen scaled differently, it comes up the wrong size: WinForms
/// re-centres a window it finds on a screen of another scale, but doesn't resize it.
/// </remarks>
internal static class ScreenScale
{
    /// <summary>MONITOR_DEFAULTTONEAREST, so a point off every screen still finds the closest one.</summary>
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>MDT_EFFECTIVE_DPI: the scale the user has set, which is what windows are sized by.</summary>
    private const int EffectiveDpi = 0;

    /// <summary>The scale of the screen at a point.</summary>
    /// <param name="point">A point in screen coordinates</param>
    /// <returns>The screen's dots per inch, which is 96 at 100%, or null when Windows can't say</returns>
    internal static int? At(Point point)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, MonitorDefaultToNearest);
        return GetDpiForMonitor(monitor, EffectiveDpi, out var dpi, out _) == 0 ? (int)dpi : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    // DllImport rather than LibraryImport, matching the rest of the app.
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
