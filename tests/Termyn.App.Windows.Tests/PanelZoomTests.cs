using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The description panel keeping the scale it was given when it draws again.
/// </summary>
/// <remarks>
/// A rich edit control goes back to its own size whenever it's handed a new document, and both
/// halves of the panel are handed one on every task switch, sync and pause in the typing — so a
/// panel the user had scaled went back to its own size within moments of their scaling it.
/// </remarks>
public class PanelZoomTests
{
    private const int WmMouseWheel = 0x020A;
    private const int MkControl = 0x0008;
    private const int WheelDelta = 120;
    private const int EmGetFirstVisibleLine = 0x00CE;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

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
        var view = Find<MarkdownView>(window);
        Assert.Equal("What the first one is about", view.Markdown);

        CtrlWheel(view, notches: 2);
        var wheeled = view.ZoomFactor;
        Assert.NotEqual(1f, wheeled);

        Find<OutlineView>(window).SelectId("b");

        Assert.Equal("What the second one is about", view.Markdown);
        Assert.Equal(wheeled, view.ZoomFactor);
        Assert.True(window.NewContext(null).Zoomed);
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

        _ = Find<OutlineView>(window).Handle;
        _ = Find<MarkdownView>(window).Handle;
        _ = Find<MarkdownEditor>(window).Handle;

        Find<OutlineView>(window).SelectId("a");
        return window;
    }

    private static InMemorySnapshotStore Account()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"First","description":"What the first one is about","project_id":"p1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Second","description":"What the second one is about","project_id":"p1","child_order":2}""");
        return store;
    }

    /// <summary>A description long enough to scroll, one paragraph to a line.</summary>
    private static string Lines(int count)
        => string.Join('\n', Enumerable.Range(1, count).Select(n => $"Line {n}"));

    /// <summary>Turns the wheel with Ctrl held, which the control answers by scaling itself.</summary>
    private static void CtrlWheel(RichTextBox box, int notches)
    {
        for (var i = 0; i < notches; i++)
            SendMessage(box.Handle, WmMouseWheel, (WheelDelta << 16) | MkControl, 0);
    }

    private static int FirstVisibleLine(RichTextBox box)
        => (int)SendMessage(box.Handle, EmGetFirstVisibleLine, 0, 0);

    private static T Find<T>(Control parent) where T : Control => Descendants(parent).OfType<T>().Single();

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;

            foreach (var below in Descendants(child))
                yield return below;
        }
    }
}
