using System.Runtime.InteropServices;

namespace Termyn.App.Windows;

/// <summary>
/// A tip for small things inside one control, each of which says something different.
/// </summary>
/// <remarks>
/// Shown by hand rather than handed the control with SetToolTip. That makes the whole control one
/// tip as far as Windows is concerned, and Windows shows a tip once per visit: moving on to another
/// target left the first one's words up, and a click put the tip away until the pointer left the
/// control altogether. So the pauses are run here, from the toolkit's own delays, and the tip's put
/// up and taken down here — including when it's been up long enough, which Windows would otherwise
/// do behind this class's back.
/// </remarks>
internal sealed class HoverTip : IDisposable
{
    private readonly Control _owner;

    /// <summary>What's under a point: something to tell targets apart by, and what its tip says.</summary>
    private readonly Func<Point, (string Key, string Text)?> _lookup;

    /// <summary>
    /// The tip itself. Ampersands are read the way a menu reads them, so text shared with a menu
    /// item doesn't show its access key's marker.
    /// </summary>
    private readonly ToolTip _tip = new() { StripAmpersands = true };

    /// <summary>Counts down the pause before the tip comes up, and then how long it stays.</summary>
    private readonly System.Windows.Forms.Timer _timer = new();

    /// <summary>The target the pointer's on, or null when it's on none.</summary>
    private (string Key, string Text)? _on;

    /// <summary>Where the pointer was last seen, in the owner's own coordinates.</summary>
    private Point _pointer;

    /// <summary>Whether the pointer's over the owner at all, as far as its moves and leaving say.</summary>
    private bool _inside;

    private bool _up;

    private bool _enabled = true;

    /// <summary>Sets up the tip for a control.</summary>
    /// <param name="owner">The control the targets are in</param>
    /// <param name="lookup">What's under a point, or null where there's nothing to say</param>
    public HoverTip(Control owner, Func<Point, (string Key, string Text)?> lookup)
    {
        _owner = owner;
        _lookup = lookup;
        _timer.Tick += (_, _) => Elapse();
    }

    /// <summary>
    /// How long the pointer rests on a target before the first tip comes up, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Half as long again as the toolkit's own pause, which was a touch quick in use. Moving on
    /// from one target to the next still takes the toolkit's short pause.
    /// </remarks>
    public int FirstPause => _tip.InitialDelay * 3 / 2;

    /// <summary>Whether there are tips at all. Switching them off takes down one that's up.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
                Drop();
        }
    }

    /// <summary>What the pointer's on, for a test to read.</summary>
    internal string? Aimed => _on?.Text;

    /// <summary>What the tip is showing, or null when it's down, for a test to read.</summary>
    internal string? Showing => _up ? _on?.Text : null;

    /// <summary>How long the timer's counting down, or null when it isn't, for a test to read.</summary>
    internal int? Pending => _timer.Enabled ? _timer.Interval : null;

    /// <summary>Whether a menu's access-key marker is kept out of the tip, for a test to read.</summary>
    internal bool StripsAmpersands => _tip.StripAmpersands;

    /// <summary>
    /// The pointer's come to a point, starting the pause when it's on a new target.
    /// </summary>
    /// <remarks>
    /// A different target counts as new even when it says the same, so the tip moves with the
    /// pointer rather than staying by something it's left. Coming off one tip straight onto another
    /// target takes the short pause, the way moving between any two tips does.
    /// </remarks>
    /// <param name="point">Where the pointer is, in the owner's own coordinates</param>
    public void PointerAt(Point point)
    {
        _pointer = point;
        _inside = true;

        var on = _enabled ? _lookup(point) : null;
        if (on == _on)
            return;

        var wasUp = _up;
        Drop();

        _on = on;
        if (on is null)
            return;

        _timer.Interval = wasUp ? _tip.ReshowDelay : FirstPause;
        _timer.Start();
    }

    /// <summary>
    /// Asks again what's under a pointer that hasn't moved, for when what's under it may have.
    /// </summary>
    /// <remarks>
    /// For whatever moves the contents rather than the pointer: a key that scrolls, the rows being
    /// replaced, the view brought round to something chosen elsewhere.
    /// </remarks>
    public void Recheck()
    {
        if (_inside)
            PointerAt(_pointer);
    }

    /// <summary>Takes the tip down and forgets the target, until the pointer next moves.</summary>
    public void Drop()
    {
        _timer.Stop();
        _on = null;

        if (!_up)
            return;

        _tip.Hide(_owner);
        _up = false;
    }

    /// <summary>The pointer's left the owner.</summary>
    public void Leave()
    {
        Drop();
        _inside = false;
    }

    /// <summary>
    /// The pause has run out, so the tip comes up — or it's been up long enough, so it goes.
    /// </summary>
    /// <remarks>
    /// Taken down here rather than by handing Show a duration, which leaves the tip to go by itself
    /// with nothing here knowing. Gone that way, the next target got the short pause meant for
    /// moving between two tips that are up. The target's kept, so resting on it doesn't bring the
    /// tip straight back; moving to another one does, after the full pause.
    ///
    /// Internal so a test can run the timer out without waiting for it.
    /// </remarks>
    internal void Elapse()
    {
        _timer.Stop();

        if (_up)
        {
            _tip.Hide(_owner);
            _up = false;
            return;
        }

        if (_on is not { } on)
            return;

        _tip.Show(on.Text, _owner, Below(_pointer));
        _up = true;

        _timer.Interval = _tip.AutoPopDelay;
        _timer.Start();
    }

    /// <summary>
    /// Where a tip goes for a pointer at a point, given relative to the owner's window as Show wants.
    /// </summary>
    /// <param name="point">Where the pointer is, in the owner's client coordinates</param>
    /// <returns>Just below the pointer as it's drawn, relative to the owner's window</returns>
    private Point Below(Point point)
    {
        var screen = _owner.PointToScreen(point);
        var window = _owner.Parent?.RectangleToScreen(_owner.Bounds).Location ?? _owner.PointToScreen(Point.Empty);

        return new Point(screen.X - window.X, screen.Y - window.Y + CursorDrop());
    }

    /// <summary>
    /// How far the pointer's image reaches below its hotspot, as Windows is drawing it now.
    /// </summary>
    /// <remarks>
    /// Read from the cursor itself rather than taken as a fixed size. Display scaling and the
    /// pointer-size setting both make it bigger, and a tip placed for the usual one sat under it.
    /// </remarks>
    /// <returns>The distance in pixels, or the system's cursor height when the cursor can't be read</returns>
    internal static int CursorDrop()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || info.Cursor == 0 || !GetIconInfo(info.Cursor, out var icon))
            return SystemInformation.CursorSize.Height;

        try
        {
            if (GetObject(icon.Mask, Marshal.SizeOf<BitmapHeader>(), out var mask) == 0)
                return SystemInformation.CursorSize.Height;

            return DropOf(mask.Height, icon.Colour != 0, icon.HotspotY);
        }
        finally
        {
            DeleteObject(icon.Mask);
            if (icon.Colour != 0)
                DeleteObject(icon.Colour);
        }
    }

    /// <summary>
    /// How far a cursor's image reaches below its hotspot, from what Windows says about it.
    /// </summary>
    /// <remarks>
    /// A cursor with no colour bitmap is a monochrome one, whose mask is two images stacked — the
    /// AND mask over the XOR — so it's twice as tall as the cursor.
    /// </remarks>
    /// <param name="maskHeight">How tall the cursor's mask bitmap is</param>
    /// <param name="hasColour">Whether it has a colour bitmap as well</param>
    /// <param name="hotspotY">How far down the image its hotspot is</param>
    /// <returns>The distance in pixels, never less than nothing</returns>
    internal static int DropOf(int maskHeight, bool hasColour, int hotspotY)
        => Math.Max(0, (hasColour ? maskHeight : maskHeight / 2) - hotspotY);

    public void Dispose()
    {
        _timer.Dispose();
        _tip.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public nint Cursor;
        public Point Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint Mask;
        public nint Colour;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint bitmap, int size, out BitmapHeader header);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
