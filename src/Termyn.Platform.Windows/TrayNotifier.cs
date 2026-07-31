using System.Drawing.Drawing2D;
using Termyn.Core.Platform;
using Termyn.Core.Settings;

namespace Termyn.Platform.Windows;

/// <summary>
/// The tray icon and its menu.
/// </summary>
/// <remarks>
/// The icon is drawn at runtime from the brand palette rather than loaded from a resource, because
/// it changes: with tasks due today it badges the count, which is the number itself at this size —
/// a tick with a tiny numeral beside it is illegible at 16 pixels.
/// </remarks>
public sealed class TrayNotifier : INotifier
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();

    private Icon? _drawn;
    private int _badged = -1;
    private bool _disposed;

    public TrayNotifier()
    {
        _icon = new NotifyIcon
        {
            Text = "Termyn",
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                Activated?.Invoke();
        };
        // Nothing is drawn here. The first icon costs the best part of a tenth of a second — GDI+
        // coming up, mostly — and doing that before the window exists is a tenth of a second added
        // to every start. The host asks for a status once it has something to show.
    }

    public event Action? Activated;

    /// <summary>The hover text as the shell has it. Internal so a test can check the truncation.</summary>
    internal string Tooltip => _icon.Text;

    /// <summary>The icon currently drawn, for a test that has no tray to look at.</summary>
    internal Icon? Icon => _drawn;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <inheritdoc />
    public void SetStatus(string tooltip, int dueToday)
    {
        if (_disposed)
            return;

        // The shell truncates anything longer, and older shells reject it outright.
        _icon.Text = tooltip.Length > 63 ? tooltip[..62] + "…" : tooltip;

        var badge = Math.Max(dueToday, 0);
        if (badge == _badged)
            return;

        var replacement = Draw(badge);
        _icon.Icon = replacement;

        // Assigned before the old one goes: disposing it while the shell still holds it blanks the
        // tray until something else forces a repaint.
        _drawn?.Dispose();
        _drawn = replacement;
        _badged = badge;
    }

    /// <inheritdoc />
    public void SetCommands(IReadOnlyList<NotifierCommand> commands)
    {
        if (_disposed)
            return;

        _menu.Items.Clear();
        foreach (var command in commands)
            _menu.Items.Add(new ToolStripMenuItem(command.Label, null, (_, _) => command.Invoke()));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _drawn?.Dispose();
    }

    /// <summary>
    /// Draws the tray icon: the brand's checkbox-and-tick, or the count of what is due today when
    /// there is something to report.
    /// </summary>
    private static Icon Draw(int dueToday)
    {
        var size = Math.Max(SystemInformation.SmallIconSize.Width, 16);
        using var bitmap = new Bitmap(size, size);

        using (var g = Graphics.FromImage(bitmap))
        {
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

            if (dueToday > 0)
            {
                // Over 99 stops being a number worth reading and becomes "a lot".
                var text = dueToday > 99 ? "99+" : dueToday.ToString();
                var points = text.Length >= 3 ? size * 0.42f : size * 0.62f;
                using var font = new Font(SystemFonts.DefaultFont.FontFamily, points, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(amber);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, brush, tile, format);
            }
            else
            {
                DrawCheckbox(g, size, amber);
            }
        }

        // Round-tripped through a handle so the result owns its own unmanaged icon rather than
        // borrowing the bitmap's, which is about to be disposed.
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

    private static void DrawCheckbox(Graphics g, int size, Color amber)
    {
        var inset = Math.Max(2, size / 8);
        var box = new Rectangle(inset, inset, size - 1 - inset * 2, size - 1 - inset * 2);

        using var pen = new Pen(amber, Math.Max(1.4f, size / 11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var outline = new Pen(amber, Math.Max(1f, size / 16f));
        using var path = RoundedRectangle(box, Math.Max(1, size / 8));
        g.DrawPath(outline, path);

        // A tick that overshoots the box on the way up, which is what makes it read as a tick at
        // this size rather than as a chevron inside a square.
        g.DrawLines(pen,
        [
            new PointF(box.Left + box.Width * 0.22f, box.Top + box.Height * 0.55f),
            new PointF(box.Left + box.Width * 0.45f, box.Top + box.Height * 0.78f),
            new PointF(box.Left + box.Width * 1.02f, box.Top + box.Height * 0.12f),
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

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
