using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Core.Settings;

namespace Termyn.Platform.Windows;

/// <summary>
/// Draws Termyn's mark: a checkbox with an amber tick on a slate tile.
/// </summary>
/// <remarks>
/// One source for every size the mark appears at — the executable's icon, the window, the installer,
/// and the tray, which badges a count over the same tile. Drawn rather than shipped as a bitmap
/// because the tray's version changes with the count, and two copies of a logo drift.
/// </remarks>
public static class BrandIcon
{
    /// <summary>The largest an icon frame can be: above this the format has no way to say the size.</summary>
    public const int MaxSize = 256;

    /// <summary>The sizes Windows asks for, smallest first. 256 is the one Explorer shows large.</summary>
    public static readonly IReadOnlyList<int> IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, MaxSize];

    /// <summary>What the badge says in place of a count over 99.</summary>
    private const string Overflow = "99+";

    /// <summary>The family the count is drawn in where it's installed, which has a real bold face.</summary>
    private const string BadgeFamilyName = "Segoe UI";

    /// <summary>
    /// The family the count is drawn in: Segoe UI, which every Windows Termyn runs on has, or the
    /// generic sans serif if it's somehow missing.
    /// </summary>
    /// <remarks>Looked up once and kept, since the tray redraws the badge every time the count moves.</remarks>
    private static readonly FontFamily BadgeFamily = InstalledFonts.Has(BadgeFamilyName)
        ? new FontFamily(BadgeFamilyName)
        : FontFamily.GenericSansSerif;

    /// <summary>
    /// Draws the mark at one size, optionally badged with a count instead of the tick.
    /// </summary>
    /// <param name="size">Edge length in pixels.</param>
    /// <param name="badge">Tasks due today. Zero draws the tick, which is the mark proper.</param>
    public static Bitmap Draw(int size, int badge = 0) => Draw(size, badge, BadgeFamily);

    /// <summary>
    /// Draws the mark at one size, with the count in a family of the caller's choosing.
    /// </summary>
    /// <remarks>So the fallback family can be drawn on a machine that has Segoe UI.</remarks>
    /// <param name="size">Edge length in pixels</param>
    /// <param name="badge">Tasks due today; zero draws the tick</param>
    /// <param name="family">The family the count is drawn in</param>
    /// <returns>The mark, which the caller owns</returns>
    internal static Bitmap Draw(int size, int badge, FontFamily family)
    {
        // Checked here rather than left to Bitmap, which answers a zero or a negative with a bare
        // "Parameter is not valid" that says nothing about which size was asked for.
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxSize);

        var bitmap = new Bitmap(size, size);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var slate = ToColor(ThemePalette.Dark.Background);
        var amber = ToColor(ThemePalette.Dark.Accent);

        // A rounded tile, so the mark reads as an app icon rather than a floating glyph.
        var tile = new Rectangle(0, 0, size - 1, size - 1);
        using (var background = new SolidBrush(slate))
        using (var path = RoundedRectangle(tile, Math.Max(2, size / 5)))
        {
            g.FillPath(background, path);
        }

        if (badge > 0)
            DrawBadge(g, tile, size, badge, amber, family);
        else
            DrawCheckbox(g, size, amber);

        return bitmap;
    }

    /// <summary>The mark as an icon that owns its own handle, for a window or the tray.</summary>
    public static Icon ToIcon(int size, int badge = 0)
    {
        using var bitmap = Draw(size, badge);

        // Round-tripped through a handle so the result owns its own unmanaged icon rather than
        // borrowing the bitmap's, which is about to go.
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// Writes a multi-resolution <c>.ico</c>. Every frame is a PNG, which the format has allowed
    /// since Vista and which keeps the 256px one from dominating the file.
    /// </summary>
    public static void WriteIcoFile(string path, IReadOnlyList<int>? sizes = null)
    {
        var wanted = sizes ?? IconSizes;

        // An empty list writes a header claiming no frames — a file that parses and then can't be
        // loaded, which is worse than refusing.
        if (wanted.Count == 0)
            throw new ArgumentException("An icon needs at least one size.", nameof(sizes));

        foreach (var size in wanted)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(size, 1, nameof(sizes));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxSize, nameof(sizes));
        }

        var frames = wanted.Select(size =>
        {
            using var bitmap = Draw(size);
            using var buffer = new MemoryStream();
            bitmap.Save(buffer, ImageFormat.Png);
            return (Size: size, Png: buffer.ToArray());
        }).ToList();

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        // ICONDIR: reserved, type 1 (icon), count.
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);

        // Each ICONDIRENTRY is 16 bytes and the images follow the whole directory.
        var offset = 6 + (frames.Count * 16);
        foreach (var (size, png) in frames)
        {
            // 256 is written as 0: the field is one byte, so it can't hold 256 itself.
            writer.Write((byte)(size >= MaxSize ? 0 : size));
            writer.Write((byte)(size >= MaxSize ? 0 : size));
            writer.Write((byte)0);  // no colour palette
            writer.Write((byte)0);  // reserved
            writer.Write((ushort)1);   // colour planes
            writer.Write((ushort)32);  // bits per pixel
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
            writer.Write(png);
    }

    private static void DrawBadge(Graphics g, Rectangle tile, int size, int badge, Color amber, FontFamily family)
    {
        // Over 99 stops being a number worth reading and becomes "a lot".
        var text = badge > 99 ? Overflow : badge.ToString();

        // Drawn as a path and placed by its ink, not by DrawString into the tile: DrawString pads
        // the text and centres the advance box, so two digits at sixteen pixels wrap onto a second
        // line, and at every size the digits sit right of centre.
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var ink = path.GetBounds();

        // A family with no outline for the digits leaves nothing to scale, and dividing by its
        // empty bounds would hand the transform an infinite scale. The bare tile is the better miss.
        if (ink.Width <= 0 || ink.Height <= 0) return;

        // As tall as the tile allows, unless that makes it wider than the tile's inside — which is
        // what shrinks "99+", and two wide digits at the small end.
        var scale = Math.Min(size * 0.5f / ink.Height, size * 0.8f / ink.Width);
        var width = ink.Width * scale;

        // Across, the box is centred and then nudged halfway toward centring the ink's mass. A
        // centred box alone leaves a "1" looking right of centre, since its flag widens the box to
        // the left of the stem the eye reads; centring the mass alone swings "10" too far the other
        // way, the "1" being so light. Not for "99+": the "+" is light enough that any nudge leans
        // the count against the tile's edge, and it fills the tile's width anyway.
        // Down, the box: every digit shares the baseline and the cap height, so that's centred.
        var centred = (tile.Width - width) / 2f;
        var balanced = (tile.Width / 2f) - ((MassCentre(path) - ink.X) * scale);
        var left = text == Overflow ? centred : (centred + balanced) / 2f;
        var top = (tile.Height - (ink.Height * scale)) / 2f;

        // Placed exactly rather than snapped to the pixel grid: snapping is sharper, but it moves
        // the count by up to half a pixel, which at twenty pixels is enough to see.
        using var matrix = new Matrix();
        matrix.Translate(left, top);
        matrix.Scale(scale, scale);
        matrix.Translate(-ink.X, -ink.Y);
        path.Transform(matrix);

        using var brush = new SolidBrush(amber);
        g.FillPath(brush, path);

        // Only Segoe UI is trusted to have a real bold. GDI+ says Microsoft Sans Serif, the generic
        // fallback, has one too, but its bold stem is barely heavier than its regular, and DrawString's
        // fake emboldening doesn't reach a path. So anything else gets its outline thickened by hand,
        // to about Segoe UI Bold's weight.
        if (family.Name != BadgeFamilyName)
        {
            using var pen = new Pen(amber, ink.Height * scale * 0.06f) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// Where a path's ink balances side to side, from the area it fills.
    /// </summary>
    /// <param name="path">The count's outline, at the size it was laid out at</param>
    /// <returns>The x coordinate of the ink's centre of mass</returns>
    private static float MassCentre(GraphicsPath path)
    {
        using var region = new Region(path);
        using var identity = new Matrix();

        float area = 0, moment = 0;
        foreach (var scan in region.GetRegionScans(identity))
        {
            var filled = scan.Width * scan.Height;
            area += filled;
            moment += filled * (scan.X + (scan.Width / 2f));
        }

        return moment / area;
    }

    private static void DrawCheckbox(Graphics g, int size, Color amber)
    {
        var inset = Math.Max(2, size / 8);
        var box = new Rectangle(inset, inset, size - 1 - (inset * 2), size - 1 - (inset * 2));

        // The tick carries the meaning and the box is only context, so the tick is the heavier of
        // the two — most at the small end, where a hairline anti-aliases into a pale smear and the
        // mark stops reading as a tick at all. Sixteen pixels is the size this mark was chosen for.
        using var pen = new Pen(amber, Math.Max(2f, size / 10f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var outline = new Pen(amber, Math.Max(1.25f, size / 18f));
        using var path = RoundedRectangle(box, Math.Max(1, size / 8));
        g.DrawPath(outline, path);

        // A tick that overshoots the box on the way up, which is what makes it read as a tick at
        // small sizes rather than as a chevron inside a square.
        g.DrawLines(pen,
        [
            new PointF(box.Left + (box.Width * 0.22f), box.Top + (box.Height * 0.55f)),
            new PointF(box.Left + (box.Width * 0.45f), box.Top + (box.Height * 0.78f)),
            new PointF(box.Left + (box.Width * 1.02f), box.Top + (box.Height * 0.12f)),
        ]);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();

        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color ToColor(Rgb rgb) => Color.FromArgb(rgb.R, rgb.G, rgb.B);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
