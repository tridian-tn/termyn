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
    /// <summary>The sizes Windows asks for, smallest first. 256 is the one Explorer shows large.</summary>
    public static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>
    /// Draws the mark at one size, optionally badged with a count instead of the tick.
    /// </summary>
    /// <param name="size">Edge length in pixels.</param>
    /// <param name="badge">Tasks due today. Zero draws the tick, which is the mark proper.</param>
    public static Bitmap Draw(int size, int badge = 0)
    {
        var bitmap = new Bitmap(size, size);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
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
            DrawBadge(g, tile, size, badge, amber);
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
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
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

    private static void DrawBadge(Graphics g, Rectangle tile, int size, int badge, Color amber)
    {
        // Over 99 stops being a number worth reading and becomes "a lot".
        var text = badge > 99 ? "99+" : badge.ToString();
        var points = text.Length >= 3 ? size * 0.42f : size * 0.62f;

        using var font = new Font(SystemFonts.DefaultFont.FontFamily, points, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(amber);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, tile, format);
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
