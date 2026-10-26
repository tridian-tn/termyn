using System.Drawing;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The line above the outline: the path to the view, and the buttons beside it.
/// </summary>
/// <remarks>
/// What the path says comes from <see cref="ViewPath"/> and is tested there. These are about the
/// line having room for it, the buttons sitting where they should, and the buttons saying the same
/// as the View menu about what there is to fold.
/// </remarks>
public class ViewHeaderTests : IDisposable
{
    /// <summary>The settings files the windows here were given, to be cleared away after.</summary>
    private readonly List<string> _settings = [];

    /// <summary>Deletes each window's settings, and whatever the store wrote alongside them.</summary>
    public void Dispose()
    {
        foreach (var name in _settings)
        foreach (var file in Directory.GetFiles(Path.GetTempPath(), name + "*"))
            File.Delete(file);

        GC.SuppressFinalize(this);
    }

    private static ViewHeader Header(int width = 600)
    {
        var header = new ViewHeader { Width = width, Theme = Theme.Resolve(ThemePreference.Light) };
        header.Fill(Menus.ViewTools, _ => { });
        header.CreateControl();
        header.PerformLayout();
        return header;
    }

    private static ToolStripButton Button(ViewHeader header, string label)
        => header.Tools.Items.OfType<ToolStripButton>().Single(b => b.Text == label);

    // ---- Room for the letters ------------------------------------------------------------------

    [WinFormsFact]
    public void The_line_grows_with_the_display_it_is_on()
    {
        // The text in it grows with the scale whatever is done here, so a line held at its 100%
        // height cuts the letters off on a scaled display.
        Assert.Equal(ViewHeader.LogicalHeight, ViewHeader.HeightAt(96));
        Assert.Equal(42, ViewHeader.HeightAt(144));
    }

    [WinFormsTheory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    public void Letters_that_drop_below_the_line_are_drawn_in_full(int dpi)
    {
        // The text at the scale's size, in the line at the scale's height, drawn the same as it is
        // in a line with room to spare. Any part of a g or a y the line cut off is ink the short one
        // doesn't have.
        using var font = new Font(Control.DefaultFont.FontFamily, Control.DefaultFont.SizeInPoints * dpi / 96f);
        const string text = "Shopping: gjpqy";

        var fitted = Ink(font, text, ViewHeader.HeightAt(dpi));
        var roomy = Ink(font, text, ViewHeader.HeightAt(dpi) * 3);

        Assert.True(fitted > 0, "the path was not drawn");
        Assert.Equal(roomy, fitted);
    }

    /// <summary>How many pixels of the path's text are drawn in a line of a given height.</summary>
    /// <param name="font">The font to draw it in</param>
    /// <param name="text">What the path says</param>
    /// <param name="height">How tall the line is</param>
    /// <returns>The pixels that aren't the background</returns>
    private static int Ink(Font font, string text, int height)
    {
        using var header = new ViewHeader { Width = 600, Height = height, Theme = Theme.Resolve(ThemePreference.Light) };
        header.Crumbs.Font = font;
        header.Crumbs.Text = text;
        header.Crumbs.Links.Clear();
        header.CreateControl();
        header.PerformLayout();

        using var shot = new Bitmap(header.Width, header.Height);
        header.DrawToBitmap(shot, new Rectangle(0, 0, header.Width, header.Height));

        var background = header.Theme.Background.ToArgb();
        var ink = 0;

        for (var y = 0; y < shot.Height; y++)
        for (var x = 0; x < header.Crumbs.Right; x++)
        {
            if (shot.GetPixel(x, y).ToArgb() != background)
                ink++;
        }

        return ink;
    }

    // ---- Where the buttons sit -----------------------------------------------------------------

    [WinFormsFact]
    public void The_buttons_sit_at_the_right_of_the_line_beside_the_path()
    {
        using var header = Header();

        Assert.Equal(header.ClientSize.Width, header.Tools.Right);
        Assert.Equal(0, header.Tools.Top);
        Assert.Equal(header.Height, header.Tools.Height);

        // The path gets what the buttons leave, and none of it is underneath them.
        Assert.Equal(0, header.Crumbs.Left);
        Assert.Equal(header.Tools.Left, header.Crumbs.Right);
    }

    [WinFormsFact]
    public void The_buttons_run_across_the_line_rather_than_down_it()
    {
        // A strip docked to a side turns vertical unless told otherwise, which on a line this tall
        // would leave room for one button and push the rest out of sight.
        using var header = Header();
        var expand = Button(header, "Expand all");
        var collapse = Button(header, "Collapse all");

        Assert.Equal(ToolStripItemPlacement.Main, expand.Placement);
        Assert.Equal(ToolStripItemPlacement.Main, collapse.Placement);
        Assert.True(collapse.Bounds.Left >= expand.Bounds.Right, $"{expand.Bounds} then {collapse.Bounds}");
        Assert.Equal(expand.Bounds.Top, collapse.Bounds.Top);
        Assert.True(header.Tools.ClientRectangle.Contains(collapse.Bounds), $"{collapse.Bounds} in {header.Tools.ClientRectangle}");
    }

    [WinFormsFact]
    public void The_buttons_grow_with_the_display_and_still_fit_the_line()
    {
        using var header = Header();

        header.ScaleTo(144);
        header.PerformLayout();

        Assert.Equal(ViewHeader.HeightAt(144), header.Height);

        foreach (var button in header.Tools.Items.OfType<ToolStripButton>())
        {
            Assert.Equal(new Size(36, 36), button.Size);
            Assert.True(header.Tools.ClientRectangle.Contains(button.Bounds), $"{button.Bounds} in {header.Tools.ClientRectangle}");
        }
    }

    // ---- Pictures, named on hover --------------------------------------------------------------

    [WinFormsFact]
    public void Each_button_is_a_picture_with_its_name_on_hover()
    {
        using var header = Header();
        header.Render(new CommandContext(CanExpandAll: true, CanCollapseAll: true));

        Assert.True(header.Tools.ShowItemToolTips);

        foreach (var button in header.Tools.Items.OfType<ToolStripButton>())
        {
            // No words drawn, but the name kept: on the tooltip, and as what a screen reader says.
            Assert.Equal(ToolStripItemDisplayStyle.None, button.DisplayStyle);
            Assert.Equal(button.Text, button.ToolTipText);
            Assert.Contains(button.Text, new[] { "Expand all", "Collapse all" });
        }
    }

    [WinFormsFact]
    public void A_greyed_button_still_says_what_it_is_on_hover()
    {
        // Greyed is when the name's most wanted: it's the question of what the button would have
        // done that's left.
        using var header = Header();
        header.Render(CommandContext.Empty);

        Assert.Equal("Expand all", Button(header, "Expand all").ToolTipText);
        Assert.Equal("Collapse all", Button(header, "Collapse all").ToolTipText);
    }

    [WinFormsFact]
    public void The_two_buttons_draw_different_pictures()
    {
        using var header = Header();
        header.Render(new CommandContext(CanExpandAll: true, CanCollapseAll: true));

        var expand = Pixels(header, Button(header, "Expand all"));
        var collapse = Pixels(header, Button(header, "Collapse all"));

        Assert.Contains(header.Theme.Text.ToArgb(), expand);
        Assert.Contains(header.Theme.Text.ToArgb(), collapse);
        Assert.NotEqual(expand, collapse);
    }

    [WinFormsFact]
    public void A_greyed_button_is_drawn_fainter_than_a_lit_one()
    {
        using var header = Header();
        header.Render(new CommandContext(CanExpandAll: true, CanCollapseAll: false));

        var text = header.Theme.Text.ToArgb();
        var background = header.Theme.Background.ToArgb();
        var greyed = Pixels(header, Button(header, "Collapse all"));

        Assert.Contains(text, Pixels(header, Button(header, "Expand all")));
        Assert.DoesNotContain(text, greyed);
        Assert.Contains(greyed, p => p != background);
    }

    [WinFormsTheory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(36)]
    public void Expand_all_is_collapse_all_with_the_minus_made_a_plus(int side)
    {
        // The same two boxes on both, and only the mark on the front one differs: everything inked
        // for Collapse all is inked for Expand all too, which has the plus's upright besides.
        var expand = Inked(side, collapse: false);
        var collapse = Inked(side, collapse: true);

        Assert.NotEmpty(collapse);
        Assert.Subset(expand, collapse);
        Assert.True(expand.Count > collapse.Count, $"{expand.Count} inked for the plus, {collapse.Count} for the minus");
    }

    /// <summary>The pixels a fold picture inks, drawn on its own in a button-sized square.</summary>
    /// <param name="side">How big the button is</param>
    /// <param name="collapse">True for Collapse all's picture, false for Expand all's</param>
    /// <returns>Where the ink is</returns>
    private static HashSet<Point> Inked(int side, bool collapse)
    {
        using var shot = new Bitmap(side, side);
        using (var g = Graphics.FromImage(shot))
        {
            g.Clear(Color.White);
            FoldGlyph.Draw(g, new Rectangle(0, 0, side, side), collapse, Color.Black);
        }

        var inked = new HashSet<Point>();
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            if (shot.GetPixel(x, y).ToArgb() != Color.White.ToArgb())
                inked.Add(new Point(x, y));
        }

        return inked;
    }

    /// <summary>A button's pixels, row by row, as the header draws it.</summary>
    /// <param name="header">The header it's on</param>
    /// <param name="button">The button</param>
    /// <returns>Each pixel's colour</returns>
    private static int[] Pixels(ViewHeader header, ToolStripItem button)
    {
        using var shot = new Bitmap(header.Width, header.Height);
        header.DrawToBitmap(shot, new Rectangle(0, 0, header.Width, header.Height));

        var bounds = button.Bounds;
        bounds.Offset(header.Tools.Location);

        var pixels = new List<int>();
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
            pixels.Add(shot.GetPixel(x, y).ToArgb());

        return [.. pixels];
    }

    [WinFormsFact]
    public void A_command_with_no_picture_is_refused_rather_than_drawn_blank()
    {
        // So adding the next button to the line can't be done without drawing it something.
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewHeader.GlyphFor(AppCommand.Undo));
    }

    [WinFormsFact]
    public void The_buttons_are_out_of_the_tab_order()
    {
        // The View menu and the keyboard already reach both; a stop here would only mean pressing
        // Tab once more on the way from the search box to the list.
        using var header = Header();

        Assert.False(header.Tools.TabStop);
    }

    // ---- Saying the same as the View menu ------------------------------------------------------

    [WinFormsTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Each_button_is_lit_exactly_when_its_View_menu_entry_is(bool canExpand, bool canCollapse)
    {
        var context = new CommandContext(CanExpandAll: canExpand, CanCollapseAll: canCollapse);

        using var header = Header();
        header.Render(context);

        using var menu = new ContextMenuStrip();
        MainForm.FillMenu(menu.Items, Menus.Bar, context, _ => string.Empty, _ => { });
        var view = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "&View");

        foreach (var button in header.Tools.Items.OfType<ToolStripButton>())
        {
            var entry = view.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == button.Text);
            Assert.Equal(entry.Enabled, button.Enabled);
        }
    }

    [WinFormsFact]
    public void A_click_runs_the_buttons_own_command()
    {
        var ran = new List<AppCommand>();
        using var header = new ViewHeader();
        header.Fill(Menus.ViewTools, ran.Add);
        header.Render(new CommandContext(CanExpandAll: true, CanCollapseAll: true));

        Button(header, "Collapse all").PerformClick();
        Button(header, "Expand all").PerformClick();

        Assert.Equal([AppCommand.CollapseAll, AppCommand.ExpandAll], ran);
    }

    // ---- In the window -------------------------------------------------------------------------

    /// <summary>A parent with a child under it, so there's something to fold.</summary>
    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Child","project_id":"p","parent_id":"a","child_order":1}""");
        return store;
    }

    /// <summary>
    /// A window over <see cref="Store"/>, shown off-screen: one that was never displayed has no
    /// handle, and drops the renders these are about rather than running them.
    /// </summary>
    /// <remarks>
    /// Its settings file is named afresh each time, so nothing one run saves is read back by the
    /// next, and it's kept off the taskbar so a test run doesn't flash windows there.
    /// </remarks>
    /// <param name="presenter">The presenter, to drive the window with</param>
    /// <returns>The window, which the caller disposes</returns>
    private MainForm Shown(out MainPresenter presenter)
    {
        var name = $"termyn-view-header-{Guid.NewGuid():N}.json";
        _settings.Add(name);

        var window = TestWindow.Build(name, Store(), out _, out presenter);
        TestWindow.ShowOffScreen(window);
        return window;
    }

    [WinFormsFact]
    public void The_buttons_fold_the_outline_and_keep_up_with_it()
    {
        using var window = Shown(out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));
        var header = TestWindow.Find<ViewHeader>(window);
        var expand = Button(header, "Expand all");
        var collapse = Button(header, "Collapse all");

        // Everything open: only the folding away is on offer.
        Assert.False(expand.Enabled);
        Assert.True(collapse.Enabled);

        collapse.PerformClick();

        Assert.Equal(["Parent"], presenter.Rows.Select(r => r.Content));
        Assert.True(expand.Enabled);
        Assert.False(collapse.Enabled);

        // Opened the way a row's own expander does it rather than from the buttons, which still
        // follow it.
        presenter.SetCollapsed("a", false);

        Assert.Equal(["Parent", "Child"], presenter.Rows.Select(r => r.Content));
        Assert.False(expand.Enabled);
        Assert.True(collapse.Enabled);
    }

    [WinFormsFact]
    public void A_view_with_nothing_to_fold_offers_neither()
    {
        // Lit first, so a header that was never rendered can't pass by never having been lit.
        using var window = Shown(out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));
        var header = TestWindow.Find<ViewHeader>(window);
        Assert.True(Button(header, "Collapse all").Enabled);

        // Today, which holds neither task: nothing's due.
        presenter.Select(ViewSelection.Of(SmartView.Today));
        Assert.Empty(presenter.Rows);

        Assert.False(Button(header, "Expand all").Enabled);
        Assert.False(Button(header, "Collapse all").Enabled);
    }

    // ---- Scaled with the display ---------------------------------------------------------------

    [WinFormsFact]
    public void The_window_sizes_its_fixed_strips_for_the_display_it_is_told_of()
    {
        // The status line and the panel's own header had the line above the outline's bug: a fixed
        // 24 pixels while their text grew. The window sizes all three together now.
        using var window = Shown(out _);
        var header = TestWindow.Find<ViewHeader>(window);
        var panel = TestWindow.Find<DetailHeader>(window);
        var status = window.Controls.OfType<System.Windows.Forms.Label>().Single(l => l.Dock == DockStyle.Bottom);

        window.SizeStrips(144);

        Assert.Equal(ViewHeader.HeightAt(144), header.Height);
        Assert.All(header.Tools.Items.OfType<ToolStripButton>(), b => Assert.Equal(ViewHeader.ButtonSizeAt(144), b.Size));
        Assert.Equal(36, panel.Height);
        Assert.Equal(36, status.Height);
    }

    [WinFormsFact]
    public void Being_moved_to_a_display_of_another_scale_resizes_the_strips()
    {
        // Through the window's own handler for a change of screen, rather than calling the sizing
        // directly, so this fails if the handler stops asking for it. Raised by hand: the test
        // host isn't aware of per-screen scales, so Windows never sends it the real message.
        using var window = Shown(out _);
        var header = TestWindow.Find<ViewHeader>(window);
        var panel = TestWindow.Find<DetailHeader>(window);
        var was = window.DeviceDpi;
        var to = was == 144 ? 192 : 144;

        try
        {
            DpiChanged(window, was, to);

            Assert.Equal(ViewHeader.HeightAt(to), header.Height);
            Assert.Equal(StartupLayout.Scaled(DetailHeader.LogicalHeight, to), panel.Height);
        }
        finally
        {
            DpiChanged(window, to, was);
        }
    }

    /// <summary>Runs a window's handler for being moved to a screen of another scale.</summary>
    /// <remarks>
    /// The event's arguments can only be made the way the framework makes them, from the message
    /// Windows sends, so they're built from one by reflection. It carries the window's own bounds as
    /// the suggested ones, so the window stays where it is.
    /// </remarks>
    /// <param name="window">The window</param>
    /// <param name="from">The old scale's dots per inch</param>
    /// <param name="to">The new scale's dots per inch</param>
    private static void DpiChanged(Form window, int from, int to)
    {
        const int WmDpiChanged = 0x02E0;
        const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var bounds = window.Bounds;
        var pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int) * 4);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }, 0, pointer, 4);
            var message = Message.Create(window.Handle, WmDpiChanged, (nint)((to << 16) | to), pointer);
            var args = (DpiChangedEventArgs)Activator.CreateInstance(typeof(DpiChangedEventArgs), Hidden, null, [from, message], null)!;

            typeof(Form).GetMethod("OnDpiChanged", Hidden)!.Invoke(window, [args]);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);
        }
    }

    // ---- The first click -----------------------------------------------------------------------

    [WinFormsFact]
    public void The_click_that_brings_the_window_forward_is_let_through()
    {
        // Without this the strip answers a click that lands while another app is in front by
        // activating the window and throwing the click away. Which it does depends on where the
        // real pointer is, which a test can't set without moving the user's, so the switch is
        // what's checked rather than a click.
        using var header = Header();

        Assert.True(header.Tools.AllowClickThrough);
    }

    // ---- The picture's geometry ----------------------------------------------------------------

    [WinFormsTheory]
    [InlineData(24)]
    [InlineData(27)]
    [InlineData(30)]
    [InlineData(36)]
    [InlineData(42)]
    [InlineData(48)]
    [InlineData(60)]
    public void The_plus_sits_square_in_the_middle_of_its_box(int side)
    {
        // Whole pixels either side of the middle, both ways, at every size. On a grid that didn't
        // match the picture's size the upright fell half a pixel left of the minus's middle at 100%.
        var shape = FoldGlyph.Layout(new Rectangle(0, 0, side, side));

        Assert.Equal(shape.Front.Left + shape.Front.Right, shape.Centre.X * 2);
        Assert.Equal(shape.Front.Top + shape.Front.Bottom, shape.Centre.Y * 2);
        Assert.InRange(shape.Arm, 1, (shape.Front.Width / 2) - 2);
    }

    [WinFormsTheory]
    [InlineData(24, 1)]
    [InlineData(36, 2)]
    [InlineData(48, 2)]
    [InlineData(60, 3)]
    public void The_lines_thicken_as_the_picture_grows(int side, int stroke)
    {
        // 60 is a button at 250%: rounded to even it came out at 2, as thin as 150%'s.
        Assert.Equal(stroke, FoldGlyph.Layout(new Rectangle(0, 0, side, side)).Stroke);
    }
}
