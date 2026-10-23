using System.ComponentModel;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The line above the outline: the path to the view on the left, and a strip of buttons about the
/// whole view on the right.
/// </summary>
/// <remarks>
/// A panel rather than the path's label on its own, so the two can share the line, the path taking
/// whatever the strip leaves it. The buttons are menu entries drawn another way: what they're called
/// and whether they're lit come from <see cref="Presentation.Commands.StateOf"/>, the same as the
/// View menu, so the two can't disagree about what there is to do. Each is a picture rather than
/// words, with its name in the tooltip.
/// </remarks>
internal sealed class ViewHeader : Panel
{
    /// <summary>
    /// How tall the line is at 100%.
    /// </summary>
    /// <remarks>
    /// It was 24 at every scale. The text grows with the display and the line didn't, so from 125%
    /// up the tails of g, p and y were cut off — by a third at 150%. It's scaled now, and a little
    /// taller so they've room to spare.
    /// </remarks>
    internal const int LogicalHeight = 28;

    /// <summary>How wide and tall a button is at 100%, which leaves a little of the line either side.</summary>
    internal const int LogicalButtonSide = 24;

    private Theme _theme = Theme.Resolve(ThemePreference.System);

    /// <summary>The display scale the line was last sized for, so buttons added later match it.</summary>
    private int _dpi = 96;

    public ViewHeader()
    {
        Dock = DockStyle.Top;
        Height = LogicalHeight;

        Crumbs = new LinkLabel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,

            // Centred rather than padded down from the top, so the path sits on the same line as the
            // buttons' text whatever height the line is scaled to.
            Padding = new Padding(6, 0, 6, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            LinkBehavior = LinkBehavior.HoverUnderline,
            UseCompatibleTextRendering = false,
        };

        Tools = new ToolStrip
        {
            Dock = DockStyle.Right,

            // Said outright: left to itself a strip docked to a side turns vertical, and stacks its
            // buttons down a line that has room for one.
            LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow,
            GripStyle = ToolStripGripStyle.Hidden,
            CanOverflow = false,
            Padding = new Padding(0, 0, 4, 0),
            TabStop = false,

            // The buttons are pictures, so this is where their names are read.
            ShowItemToolTips = true,
        };

        // The path first and the strip after, so the strip takes the right-hand edge and the path
        // fills what's left: a docked control added later sits nearer the edge.
        Controls.Add(Crumbs);
        Controls.Add(Tools);
        ApplyTheme();
    }

    /// <summary>The path to the view, every step but the last a link back up to it.</summary>
    public LinkLabel Crumbs { get; }

    /// <summary>The buttons, right-aligned beside the path.</summary>
    public ToolStrip Tools { get; }

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
    /// How tall the line is at a display's scale.
    /// </summary>
    /// <param name="dpi">The display's dots per inch, which is 96 at 100%</param>
    /// <returns>The height in the display's pixels</returns>
    internal static int HeightAt(int dpi) => StartupLayout.Scaled(LogicalHeight, dpi);

    /// <summary>
    /// Sizes the line and its buttons for a display's scale.
    /// </summary>
    /// <remarks>
    /// The buttons are given their size rather than left to work it out. A picture has no text to
    /// measure, and the strip keeps the size an item had the last time it was laid out, so a button
    /// sized from its scale at the time wouldn't follow the window to another screen.
    /// </remarks>
    /// <param name="dpi">The display's dots per inch, which is 96 at 100%</param>
    public void ScaleTo(int dpi)
    {
        _dpi = dpi;
        Height = HeightAt(dpi);

        foreach (var button in Tools.Items.OfType<GlyphButton>())
            button.Size = ButtonSizeAt(dpi);
    }

    /// <summary>How big a button is at a display's scale.</summary>
    /// <param name="dpi">The display's dots per inch, which is 96 at 100%</param>
    /// <returns>The button's size in the display's pixels</returns>
    internal static Size ButtonSizeAt(int dpi)
    {
        var side = StartupLayout.Scaled(LogicalButtonSide, dpi);
        return new Size(side, side);
    }

    /// <summary>
    /// Puts up a button for each entry, greyed until <see cref="Render"/> says otherwise.
    /// </summary>
    /// <param name="entries">The buttons to show, in order</param>
    /// <param name="run">Called with the command of whichever button is clicked</param>
    public void Fill(IReadOnlyList<MenuEntry> entries, Action<AppCommand> run)
    {
        foreach (var entry in entries)
        {
            if (entry.SeparatorBefore && Tools.Items.Count > 0)
                Tools.Items.Add(new ToolStripSeparator());

            var button = new GlyphButton(GlyphFor(entry.Command))
            {
                Tag = entry.Command,
                DisplayStyle = ToolStripItemDisplayStyle.None,
                AutoSize = false,
                Size = ButtonSizeAt(_dpi),

                // Said by Render along with the name, so the two can't drift apart.
                AutoToolTip = false,
            };

            button.Click += (_, _) => run(entry.Command);
            Tools.Items.Add(button);
        }

        Render(CommandContext.Empty);
    }

    /// <summary>
    /// Names and lights each button for what can be done now.
    /// </summary>
    /// <remarks>
    /// The name goes on the button as well as in its tooltip, though it isn't drawn: it's what a
    /// screen reader says the button is called.
    /// </remarks>
    /// <param name="context">What's selected, the same as the menus read from</param>
    public void Render(CommandContext context)
    {
        foreach (var button in Tools.Items.OfType<GlyphButton>())
        {
            var state = Presentation.Commands.StateOf((AppCommand)button.Tag!, context);
            button.Text = state.Label;
            button.ToolTipText = state.Label;
            button.Enabled = state.Enabled;
        }
    }

    /// <summary>
    /// The picture a command's button is drawn with.
    /// </summary>
    /// <remarks>
    /// Thrown rather than falling back to something, so a command added to the strip without a
    /// picture to go with it fails the first time the window's built rather than turning up blank.
    /// </remarks>
    /// <param name="command">The button's command</param>
    /// <returns>What draws it, given where and in what colour</returns>
    internal static Action<Graphics, Rectangle, Color> GlyphFor(AppCommand command) => command switch
    {
        AppCommand.ExpandAll => (g, bounds, colour) => FoldGlyph.Draw(g, bounds, collapse: false, colour),
        AppCommand.CollapseAll => (g, bounds, colour) => FoldGlyph.Draw(g, bounds, collapse: true, colour),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "There's no picture drawn for this command."),
    };

    /// <summary>A button drawn as a picture of what it does.</summary>
    /// <param name="draw">Draws the picture, given where and in what colour</param>
    private sealed class GlyphButton(Action<Graphics, Rectangle, Color> draw) : ToolStripButton
    {
        /// <summary>Draws the picture, given where and in what colour.</summary>
        public Action<Graphics, Rectangle, Color> Draw { get; } = draw;
    }

    /// <summary>Puts the current colours on the path and the strip.</summary>
    private void ApplyTheme()
    {
        BackColor = _theme.Background;

        // The step you're on is the one being read; the ones above it are offers, and drawn as
        // links so they read as such without needing to be hovered to find out.
        Crumbs.BackColor = _theme.Background;
        Crumbs.ForeColor = _theme.Text;
        Crumbs.LinkColor = _theme.Muted;
        Crumbs.ActiveLinkColor = _theme.Accent;
        Crumbs.VisitedLinkColor = _theme.Muted;

        Tools.BackColor = _theme.Background;
        Tools.ForeColor = _theme.Text;
        Tools.Renderer = new FlatRenderer(_theme);
        Invalidate(invalidateChildren: true);
    }

    /// <summary>
    /// Draws the strip in the theme's colours, flat against the line it sits on.
    /// </summary>
    /// <remarks>
    /// The framework's own renderers bring a gradient, a border and a light-only hover with them,
    /// which reads as a toolbar bolted on beside the path rather than part of the same line.
    /// </remarks>
    /// <param name="theme">The colours to draw with</param>
    private sealed class FlatRenderer(Theme theme) : ToolStripRenderer
    {
        /// <summary>
        /// The picture on a button with nothing to do.
        /// </summary>
        /// <remarks>
        /// Halfway from the quiet text colour to the background. The quiet colour itself is what the
        /// path's links are drawn in, so a greyed button in it would look like something to click.
        /// </remarks>
        private readonly Color _disabled = Color.FromArgb(
            (theme.Muted.R + theme.Background.R) / 2,
            (theme.Muted.G + theme.Background.G) / 2,
            (theme.Muted.B + theme.Background.B) / 2);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(theme.Background);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        /// <summary>
        /// Draws a button: lit under the pointer, then its picture over that.
        /// </summary>
        /// <remarks>
        /// The picture's drawn here rather than handed over as an image, so it's drawn at whatever
        /// size the button is and in the theme's colours, rather than scaled and greyed by the
        /// framework into something that matches neither.
        /// </remarks>
        /// <param name="e">The button, and where to draw it</param>
        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);

            if (e.Item.Enabled && (e.Item.Selected || e.Item.Pressed))
            {
                using var brush = new SolidBrush(e.Item.Pressed ? theme.Border : theme.Row);
                e.Graphics.FillRectangle(brush, bounds);
            }

            if (e.Item is GlyphButton button)
                button.Draw(e.Graphics, bounds, e.Item.Enabled ? theme.Text : _disabled);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(theme.Border);
            var x = e.Item.Width / 2;
            e.Graphics.DrawLine(pen, x, 4, x, e.Item.Height - 4);
        }
    }
}
