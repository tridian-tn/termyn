using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>
/// The tray icon and its menu.
/// </summary>
/// <remarks>
/// The icon is drawn at runtime rather than loaded from a resource, because it changes: with tasks
/// due today it badges the count, which is the number itself at this size — a tick with a tiny
/// numeral beside it is illegible at 16 pixels. The drawing itself lives in <see cref="BrandIcon"/>,
/// so the tray and the executable's own icon can't drift apart.
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

        var replacement = BrandIcon.ToIcon(Math.Max(SystemInformation.SmallIconSize.Width, 16), badge);
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

}
