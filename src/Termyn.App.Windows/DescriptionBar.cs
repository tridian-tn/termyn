using System.ComponentModel;
using Termyn.Core.Settings;
using Termyn.Platform.Windows;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The strip across the top of the description, with the pencil that switches it between the
/// markdown and the rendering, and room after it for formatting buttons when there are some.
/// </summary>
/// <remarks>
/// One button that stays pressed while the markdown is showing, like a Bold button stays pressed in
/// bold text, rather than a pair. It's the Edit description entry in the View menu drawn another way:
/// what it's called and whether it's lit come from <see cref="Presentation.Commands.StateOf"/>, so
/// the two can't disagree.
///
/// On the left, where the eye starts along the strip and where formatting buttons would follow it.
///
/// A strip of buttons rather than an ordinary button because clicking a strip's button leaves the
/// focus where it was. An ordinary one would take it from the markdown on the way down, and the
/// markdown losing the focus is what saves it and puts the rendering back, before the click had said
/// what it wanted.
/// </remarks>
internal sealed class DescriptionBar : Panel
{
    /// <summary>How tall the strip is at 100%.</summary>
    internal const int LogicalHeight = 28;

    /// <summary>How wide and tall a button is at 100%, the same as the ones above the outline.</summary>
    internal const int LogicalButtonSide = ViewHeader.LogicalButtonSide;

    /// <summary>
    /// The icon fonts Windows draws its own toolbar pictures with, newest first.
    /// </summary>
    /// <remarks>
    /// Checked by name, since a font that isn't installed is fallen back on silently and the pencil
    /// would come out as an empty box. Where neither is on the machine, the pencil from the ordinary
    /// symbols stands in.
    /// </remarks>
    private static readonly string[] IconFonts = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    /// <summary>Edit, the pencil, in those fonts: U+E70F, which shows as nothing in most editors.</summary>
    private const string IconPencil = "";

    private const string PlainPencil = "✎";

    private Theme _theme = Theme.Resolve(ThemePreference.System);
    private int _dpi = 96;

    public DescriptionBar()
    {
        Dock = DockStyle.Top;
        Height = LogicalHeight;

        // The bottom row kept back for the rule, the same as the line naming the panel's subject.
        Padding = new Padding(0, 0, 0, 1);

        Tools = new ToolStrip
        {
            Dock = DockStyle.Fill,

            // Without it the click that brings the window forward is eaten by the strip, the same
            // as the buttons above the outline.
            AllowClickThrough = true,
            LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow,
            GripStyle = ToolStripGripStyle.Hidden,
            CanOverflow = false,
            Padding = new Padding(4, 0, 0, 0),
            TabStop = false,
            ShowItemToolTips = true,
        };

        Controls.Add(Tools);
        ApplyTheme();
    }

    /// <summary>The buttons, for a test to click.</summary>
    internal ToolStrip Tools { get; }

    /// <summary>The pencil, once <see cref="Fill"/> has put it up.</summary>
    internal ToolStripButton? Pencil { get; private set; }

    /// <summary>The colours to draw with.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            ApplyTheme();
        }
    }

    /// <summary>
    /// Puts up the pencil, greyed until <see cref="Render"/> says otherwise.
    /// </summary>
    /// <param name="command">The command the pencil runs</param>
    /// <param name="run">Called with it when the pencil's clicked</param>
    public void Fill(AppCommand command, Action<AppCommand> run)
    {
        Pencil = new ToolStripButton
        {
            Tag = command,
            DisplayStyle = ToolStripItemDisplayStyle.None,
            AutoSize = false,
            Size = ButtonSizeAt(_dpi),
            AutoToolTip = false,
            Enabled = false,
        };

        Pencil.Click += (_, _) => run(command);
        Tools.Items.Add(Pencil);
    }

    /// <summary>
    /// Names, lights and presses the pencil for what the description is doing now.
    /// </summary>
    /// <remarks>
    /// The name goes on the button as well as in its tooltip, though it isn't drawn: it's what a
    /// screen reader says the button is called. The tooltip adds the key, since a picture can't say
    /// it and a menu entry can.
    /// </remarks>
    /// <param name="state">The command's state, as the View menu would show it</param>
    public void Render(CommandState state)
    {
        if (Pencil is null)
            return;

        var key = Shortcuts.ShortcutFor((AppCommand)Pencil.Tag!);

        Pencil.Text = state.Label;
        Pencil.ToolTipText = key.Length == 0 ? state.Label : $"{state.Label} ({key})";
        Pencil.Enabled = state.Enabled;
        Pencil.Checked = state.Checked;
    }

    /// <summary>Sizes the strip and the pencil for a display's scale.</summary>
    /// <param name="dpi">The display's dots per inch, which is 96 at 100%</param>
    public void ScaleTo(int dpi)
    {
        _dpi = dpi;
        Height = StartupLayout.Scaled(LogicalHeight, dpi);

        if (Pencil is not null)
            Pencil.Size = ButtonSizeAt(dpi);
    }

    /// <summary>Draws the rule between the strip and the description under it.</summary>
    /// <param name="e">Where to draw</param>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var pen = new Pen(_theme.Border);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    private static Size ButtonSizeAt(int dpi)
    {
        var side = StartupLayout.Scaled(LogicalButtonSide, dpi);
        return new Size(side, side);
    }

    private void ApplyTheme()
    {
        BackColor = _theme.Panel;
        Tools.BackColor = _theme.Panel;
        Tools.ForeColor = _theme.Text;
        Tools.Renderer = new FlatRenderer(_theme);
        Invalidate(invalidateChildren: true);
    }

    /// <summary>
    /// Draws a button flat on the strip: lit under the pointer, filled while it's pressed in, and
    /// the pencil over that.
    /// </summary>
    /// <param name="theme">The colours to draw with</param>
    private sealed class FlatRenderer(Theme theme) : ToolStripRenderer
    {
        /// <summary>Halfway from the quiet text to the background, for a button with nothing to do.</summary>
        private readonly Color _disabled = Theme.ToColor(
            ThemePalette.Blend(Theme.ToRgb(theme.Muted), Theme.ToRgb(theme.Panel), 0.5));

        /// <summary>The font the pencil's drawn in, or null where only the plain one is to hand.</summary>
        private static readonly string? IconFont = IconFonts.FirstOrDefault(InstalledFonts.Has);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(theme.Panel);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        /// <summary>
        /// Draws the button's background for its state, then the pencil.
        /// </summary>
        /// <remarks>
        /// Pressed in, it's filled the way a hovered row is and the pencil goes to the accent, so it
        /// reads as on from across the panel and not only under the pointer.
        /// </remarks>
        /// <param name="e">The button, and where to draw it</param>
        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            var on = e.Item is ToolStripButton { Checked: true };

            if (e.Item.Enabled && (on || e.Item.Selected || e.Item.Pressed))
            {
                using var brush = new SolidBrush(e.Item.Pressed ? theme.Border : theme.Row);
                e.Graphics.FillRectangle(brush, bounds);
            }

            var colour = !e.Item.Enabled ? _disabled : on ? theme.Accent : theme.Text;
            DrawPencil(e.Graphics, bounds, colour);
        }

        /// <summary>The pencil, centred in the button at about two thirds of its height.</summary>
        private static void DrawPencil(Graphics g, Rectangle bounds, Color colour)
        {
            var pixels = Math.Max(8, bounds.Height * 2 / 3);
            using var font = new Font(IconFont ?? Control.DefaultFont.FontFamily.Name, pixels * 0.75f, GraphicsUnit.Pixel);

            TextRenderer.DrawText(
                g,
                IconFont is null ? PlainPencil : IconPencil,
                font,
                bounds,
                colour,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}
