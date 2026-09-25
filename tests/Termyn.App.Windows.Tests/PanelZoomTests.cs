using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The description panel's scale: kept when the panel draws again, and put back when it's asked to be.
/// </summary>
/// <remarks>
/// A rich edit control goes back to its own size whenever it's handed a new document, and both
/// halves of the panel are handed one on every task switch, sync and pause in the typing — so a
/// panel the user had scaled went back to its own size within moments of their scaling it.
///
/// Putting it back had the opposite trouble. The control's zoom property remembers the last scale
/// it set and skips a set that matches, and the wheel scales the control without going through it —
/// so after the wheel, Ctrl+0 asked for the size the property thought it was still at.
/// </remarks>
public class PanelZoomTests
{
    private const int WmMouseWheel = 0x020A;
    private const int MkControl = 0x0008;
    private const int WheelDelta = 120;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmGetZoom = 0x0400 + 224;
    private const int EmSetZoom = 0x0400 + 225;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, ref int wParam, ref int lParam);

    // ---- The rendering ------------------------------------------------------------------------

    [WinFormsFact]
    public void A_scaled_rendering_keeps_its_scale_when_the_description_changes()
    {
        // Set through the property, as the menu sets it. The property remembers what it last set
        // and skips setting it again, so this also checks the scale goes back by some other road.
        using var view = Rendering("The first task's description");
        view.ZoomFactor = 1.5f;

        view.Markdown = "The **second** task's description";

        Assert.Equal(1.5f, view.ZoomFactor);
    }

    [WinFormsFact]
    public void A_scaled_rendering_keeps_its_scale_when_the_theme_changes()
    {
        using var view = Rendering("A description");
        view.ZoomFactor = 1.5f;

        view.Theme = Theme.Resolve(ThemePreference.Dark);

        Assert.Equal(1.5f, view.ZoomFactor);
    }

    [WinFormsFact]
    public void A_scale_the_wheel_chose_is_kept_as_well()
    {
        // The wheel scales the control without going near the property, so there's nothing but the
        // control itself that knows about it.
        using var view = Rendering("A description");
        CtrlWheel(view, notches: 2);
        var wheeled = view.ZoomFactor;
        Assert.NotEqual(1f, wheeled);

        view.Markdown = "Another description";

        Assert.Equal(wheeled, view.ZoomFactor);
    }

    [WinFormsFact]
    public void A_scaled_rendering_still_opens_at_the_top()
    {
        // Setting the scale lays the document out again, and it's put back after the scroll to the
        // top — which has to survive it.
        using var view = Rendering(Lines(80));
        view.Size = new Size(300, 120);
        view.ZoomFactor = 1.5f;
        view.Select(view.TextLength, 0);
        view.ScrollToCaret();
        Assert.NotEqual(0, FirstVisibleLine(view));

        view.Markdown = Lines(81);

        Assert.Equal(1.5f, view.ZoomFactor);
        Assert.Equal(0, FirstVisibleLine(view));
    }

    [WinFormsFact]
    public void A_link_answers_across_the_whole_height_of_a_scaled_line()
    {
        // Half again the font's height below the top of the line is past the bottom of it at the
        // font's own size, and well inside it at twice that.
        using var view = Rendering("[a link](https://example.com)");
        view.Size = new Size(300, 200);
        view.ZoomFactor = 2f;

        var top = view.GetPositionFromCharIndex(0);
        var lower = new Point(top.X + 2, top.Y + (int)(view.Font.Height * 1.5f));

        Assert.NotNull(view.LinkUnder(lower));
    }

    [WinFormsFact]
    public void The_hint_over_an_empty_pane_is_drawn_at_its_scale()
    {
        using var view = Rendering(string.Empty);
        view.ZoomFactor = 1.5f;

        using var font = ZoomLevel.ScaledFont(view);

        Assert.Equal(view.Font.Size * 1.5f, font.Size, 0.01f);
    }

    [WinFormsFact]
    public void A_box_the_wheel_scaled_can_still_be_set_through_its_property()
    {
        // The wheel goes round the property, which went on believing the box was at its own size —
        // and skipped a set back to that size as having nothing to do.
        using var view = Rendering("A description");
        CtrlWheel(view, notches: 2);

        view.ZoomFactor = 1f;

        Assert.Equal((0, 0), Zoom(view));
    }

    [WinFormsFact]
    public void A_redrawn_box_can_still_be_set_through_its_property()
    {
        // Whatever scaled the box behind the property's back, the scale going back on after a redraw
        // tells the property what it is.
        using var view = Rendering("A description");
        SendMessage(view.Handle, EmSetZoom, 150, 100);

        view.Markdown = "Another description";
        view.ZoomFactor = 1f;

        Assert.Equal((0, 0), Zoom(view));
    }

    // ---- The editor ---------------------------------------------------------------------------

    [WinFormsFact]
    public void A_scaled_editor_keeps_its_scale_when_the_styling_catches_up()
    {
        using var editor = Editing("Some words");
        editor.ZoomFactor = 1.5f;

        editor.Select(editor.TextLength, 0);
        editor.SelectedText = " and **some more**";
        editor.Restyle();

        Assert.Equal(1.5f, editor.ZoomFactor);
    }

    [WinFormsFact]
    public void A_scaled_editor_keeps_its_scale_when_a_sync_refills_it()
    {
        using var editor = Editing("Some words");
        editor.ZoomFactor = 1.5f;

        editor.Refill("Some **other** words");

        Assert.Equal(1.5f, editor.ZoomFactor);
    }

    [WinFormsFact]
    public void A_scaled_editor_keeps_its_scale_when_it_is_emptied()
    {
        // Emptying a rich edit control resets its scale where filling one doesn't, and it happens in
        // the assignment — before the styling gets the chance to take the scale and put it back.
        using var editor = Editing("Some words");
        editor.ZoomFactor = 1.5f;

        editor.Text = string.Empty;
        editor.Restyle();

        Assert.Equal(1.5f, editor.ZoomFactor);
    }

    [WinFormsFact]
    public void A_scaled_editor_keeps_its_scale_when_a_sync_empties_it()
    {
        using var editor = Editing("Some words");
        editor.ZoomFactor = 1.5f;

        editor.Refill(string.Empty);

        Assert.Equal(1.5f, editor.ZoomFactor);
    }

    [WinFormsFact]
    public void Restyling_a_scaled_editor_leaves_it_scrolled_where_it_was()
    {
        // The scroll is measured at the scale the box is drawn at. Put back at any other, it lands
        // on some other line of the description than the one being typed on.
        using var editor = Editing(Lines(80));
        editor.Size = new Size(300, 120);
        editor.ZoomFactor = 1.5f;

        editor.Select(editor.GetFirstCharIndexFromLine(40), 0);
        editor.ScrollToCaret();
        var before = FirstVisibleLine(editor);
        Assert.NotEqual(0, before);

        editor.SelectedText = "**typed** ";
        editor.Restyle();

        Assert.Equal(1.5f, editor.ZoomFactor);
        Assert.Equal(before, FirstVisibleLine(editor));
    }

    // ---- The window ---------------------------------------------------------------------------

    [WinFormsFact]
    public void Switching_task_leaves_the_panel_at_the_scale_it_was_given()
    {
        // The whole of it, as it happened: scaled with the wheel on one task, gone back to its own
        // size on the next. The window also reads the scale off the control to decide whether
        // there's a zoom to reset, so that has to go on saying there is.
        using var window = PanelOnFirstTask("termyn-panel-zoom.json");
        var view = TestWindow.Find<MarkdownView>(window);
        Assert.Equal("What the first one is about", view.Markdown);

        CtrlWheel(view, notches: 2);
        var wheeled = view.ZoomFactor;
        Assert.NotEqual(1f, wheeled);

        TestWindow.Find<OutlineView>(window).SelectId("b");

        Assert.Equal("What the second one is about", view.Markdown);
        Assert.Equal(wheeled, view.ZoomFactor);
        Assert.True(window.NewContext(null).Zoomed);
    }

    [WinFormsFact]
    public void Wheeling_one_half_brings_the_other_with_it()
    {
        using var window = PanelOnFirstTask("termyn-panel-zoom-together.json");
        var view = TestWindow.Find<MarkdownView>(window);
        var editor = TestWindow.Find<MarkdownEditor>(window);

        CtrlWheel(view, notches: 3);

        Assert.NotEqual(1f, view.ZoomFactor);
        Assert.Equal(view.ZoomFactor, editor.ZoomFactor);
    }

    [WinFormsFact]
    public void A_task_with_no_description_opens_for_writing_at_the_panels_scale()
    {
        // Both faults at once: the half you write in never saw the wheel, and emptying it for a task
        // with nothing written would have put it back to its own size if it had.
        using var window = PanelOnFirstTask("termyn-panel-zoom-empty.json");
        var view = TestWindow.Find<MarkdownView>(window);
        var editor = TestWindow.Find<MarkdownEditor>(window);

        CtrlWheel(view, notches: 3);
        var wheeled = view.ZoomFactor;
        Assert.NotEqual(1f, wheeled);

        TestWindow.Find<OutlineView>(window).SelectId("c");

        Assert.True(window.NewContext(null).WritingDescription);
        Assert.Equal(wheeled, editor.ZoomFactor);
    }

    [WinFormsFact]
    public void The_wheel_stops_where_the_menu_does()
    {
        // The control's own wheel runs from a tenth to five times. Past the menu's limits, Zoom in
        // clamped a panel back down and Zoom out pushed one back up.
        using var window = PanelOnFirstTask("termyn-panel-zoom-bounds.json");
        var view = TestWindow.Find<MarkdownView>(window);

        CtrlWheel(view, notches: 40);
        Assert.Equal(4f, view.ZoomFactor);
        window.Run(MainForm.CommandFor(Keys.Control | Keys.Oemplus, MainForm.Scope.Window));
        Assert.Equal(4f, view.ZoomFactor);

        CtrlWheel(view, notches: -80);
        Assert.Equal(0.5f, view.ZoomFactor);
        window.Run(MainForm.CommandFor(Keys.Control | Keys.OemMinus, MainForm.Scope.Window));
        Assert.Equal(0.5f, view.ZoomFactor);
    }

    // ---- Putting it back ----------------------------------------------------------------------

    [WinFormsFact]
    public void Ctrl_0_puts_a_panel_the_wheel_scaled_back_to_its_own_size()
    {
        // Nothing here reads ZoomFactor between the wheel and the reset. Reading it is what brings
        // the property's memory up to date, and that's the very thing Ctrl+0 used to go without.
        using var window = PanelOnFirstTask("termyn-panel-zoom-reset.json");
        var view = TestWindow.Find<MarkdownView>(window);

        CtrlWheel(view, notches: 2);
        Assert.NotEqual((0, 0), Zoom(view));

        window.Run(MainForm.CommandFor(Keys.Control | Keys.D0, MainForm.Scope.Window));

        Assert.Equal(1f, view.ZoomFactor);
        Assert.False(window.NewContext(null).Zoomed);
    }

    [WinFormsFact]
    public void Ctrl_0_puts_back_the_half_that_isnt_on_show_as_well()
    {
        // Scaled while it was being written in, then left for the rendering. Reset from there, it
        // came back still scaled — and the way to reset it stayed lit, since that asks both halves.
        using var window = PanelOnFirstTask("termyn-panel-zoom-reset-hidden.json");
        var editor = TestWindow.Find<MarkdownEditor>(window);
        Assert.False(window.NewContext(null).WritingDescription);

        CtrlWheel(editor, notches: 2);
        Assert.NotEqual((0, 0), Zoom(editor));

        window.Run(MainForm.CommandFor(Keys.Control | Keys.D0, MainForm.Scope.Window));

        Assert.Equal(1f, editor.ZoomFactor);
        Assert.False(window.NewContext(null).Zoomed);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static MarkdownView Rendering(string markdown)
    {
        var view = new MarkdownView { Theme = Theme.Resolve(ThemePreference.Light) };
        view.CreateControl();
        view.Markdown = markdown;
        return view;
    }

    private static MarkdownEditor Editing(string markdown)
    {
        var editor = new MarkdownEditor { Theme = Theme.Resolve(ThemePreference.Light) };
        editor.CreateControl();
        editor.Text = markdown;
        editor.Restyle();
        return editor;
    }

    /// <summary>
    /// A window with the description panel open on the first task, showing it as it reads.
    /// </summary>
    /// <remarks>
    /// The window's never shown, so nothing in it gets a handle of its own until it's asked for. The
    /// list won't take a selection without one, and neither half of the panel will draw or scale.
    /// </remarks>
    /// <param name="settingsName">A file name of its own, so two tests can't share one</param>
    /// <returns>The window, which the caller disposes</returns>
    private static MainForm PanelOnFirstTask(string settingsName)
    {
        var window = TestWindow.Build(settingsName, Account(), out _, out var presenter);
        _ = window.Handle;
        presenter.Select(ViewSelection.Of(SmartView.All));
        window.ShowPanelTab(comments: false);

        _ = TestWindow.Find<OutlineView>(window).Handle;
        _ = TestWindow.Find<MarkdownView>(window).Handle;
        _ = TestWindow.Find<MarkdownEditor>(window).Handle;

        TestWindow.Find<OutlineView>(window).SelectId("a");
        return window;
    }

    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"First","description":"What the first one is about","project_id":"p1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Second","description":"What the second one is about","project_id":"p1","child_order":2}""");
        store.PutResource("items", "c", """{"id":"c","content":"Third","project_id":"p1","child_order":3}""");
        return store;
    }

    /// <summary>A description long enough to scroll, one paragraph to a line.</summary>
    private static string Lines(int count)
        => string.Join('\n', Enumerable.Range(1, count).Select(n => $"Line {n}"));

    /// <summary>Turns the wheel with Ctrl held, which the control answers by scaling itself.</summary>
    /// <param name="box">The control under the wheel</param>
    /// <param name="notches">How far, away from you to scale up and towards you to scale down</param>
    private static void CtrlWheel(RichTextBox box, int notches)
    {
        var delta = notches > 0 ? WheelDelta : -WheelDelta;

        for (var i = 0; i < Math.Abs(notches); i++)
            SendMessage(box.Handle, WmMouseWheel, (delta << 16) | MkControl, 0);
    }

    private static int FirstVisibleLine(RichTextBox box)
        => (int)SendMessage(box.Handle, EmGetFirstVisibleLine, 0, 0);

    /// <summary>
    /// The scale as the control holds it, asked without going through its property — reading that
    /// would bring the property's memory up to date and hide what it had wrong.
    /// </summary>
    private static (int Numerator, int Denominator) Zoom(RichTextBox box)
    {
        var numerator = 0;
        var denominator = 0;
        SendMessage(box.Handle, EmGetZoom, ref numerator, ref denominator);
        return (numerator, denominator);
    }
}
