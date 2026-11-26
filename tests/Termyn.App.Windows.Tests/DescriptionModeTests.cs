using System.Runtime.InteropServices;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Getting between the description as it reads and as it's written: Edit description on Ctrl+E,
/// the pencil above the description that runs it, and the ring that says typing would change it.
/// </summary>
public class DescriptionModeTests
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int MkLButton = 0x0001;
    private const int VkShift = 0x10;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    /// <summary>Whichever control has the keyboard, so a key can be sent to it.</summary>
    private static Control Focused() => Control.FromChildHandle(GetFocus()) ?? throw new InvalidOperationException("Nothing has the keyboard.");

    /// <summary>A window on a task whose description is "Notes", with the rendering in front.</summary>
    private static MainForm Window(string settingsName, out MarkdownView view, out MarkdownEditor editor, bool shown = false)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"First","description":"Notes","project_id":"p1","child_order":1}""");

        var window = TestWindow.Build(settingsName, store, out _, out var presenter);
        if (shown)
            TestWindow.ShowOffScreen(window);

        _ = window.Handle;
        presenter.Select(ViewSelection.Of(SmartView.All));
        window.ShowPanelTab(comments: false);

        var outline = TestWindow.Find<OutlineView>(window);
        view = TestWindow.Find<MarkdownView>(window);
        editor = TestWindow.Find<MarkdownEditor>(window);
        _ = outline.Handle;
        _ = view.Handle;
        _ = editor.Handle;
        outline.SelectId("a");

        return window;
    }

    private static bool Writing(MainForm window) => window.NewContext(null).WritingDescription;

    // ---- Ctrl+E ----------------------------------------------------------------------------------

    [WinFormsFact]
    public void Ctrl_and_E_opens_the_markdown_and_only_Escape_closes_it()
    {
        // From the rendering. A read-only rich edit control takes Ctrl+E for itself, to centre the
        // paragraph, so this is also checking the window gets there first. Edit description is an
        // action like Rename, not a switch, so a second Ctrl+E leaves the markdown where it is.
        using var window = Window("termyn-ctrl-e.json", out var view, out var editor);

        Press(view, Keys.Control | Keys.E);
        Assert.True(Writing(window), "Ctrl+E didn't open the markdown");

        Press(editor, Keys.Control | Keys.E);
        Assert.True(Writing(window), "a second Ctrl+E closed the markdown");

        Press(editor, Keys.Escape);
        Assert.False(Writing(window), "Escape didn't close the markdown");
        Assert.Equal("Notes", editor.Text);
    }

    [WinFormsFact]
    public void Ctrl_and_E_from_the_outline_opens_the_markdown()
    {
        using var window = Window("termyn-ctrl-e-outline.json", out _, out _);

        Press(TestWindow.Find<OutlineView>(window), Keys.Control | Keys.E);

        Assert.True(Writing(window));
    }

    [WinFormsFact]
    public void Ctrl_and_E_opens_a_shut_panel_on_the_markdown()
    {
        // Asked for by name, so it opens the panel, where Return on a task won't.
        using var window = Window("termyn-ctrl-e-shut.json", out _, out _);
        window.Run(AppCommand.ToggleDescription);
        Assert.False(window.NewContext(null).ShowingDescription, "the panel didn't shut, so this says nothing");

        Press(TestWindow.Find<OutlineView>(window), Keys.Control | Keys.E);

        var context = window.NewContext(null);
        Assert.True(context.ShowingDescription, "the panel stayed shut");
        Assert.True(context.WritingDescription, "the panel opened on the rendering");
    }

    [WinFormsFact]
    public void Edit_description_with_nothing_to_write_to_leaves_the_panel_alone()
    {
        // The menu has it greyed, so the key does nothing either: nothing is selected, and taking
        // the comments away to show a description that can't be typed into would be a move for
        // nothing.
        using var window = TestWindow.Build("termyn-edit-nothing.json");
        _ = window.Handle;
        window.ShowPanelTab(comments: true);

        window.Run(AppCommand.EditDescription);

        Assert.True(window.NewContext(null).ShowingComments, "the comments were taken away");
    }

    [WinFormsFact]
    public void Edit_description_from_the_comments_goes_to_the_markdown()
    {
        using var window = Window("termyn-edit-from-comments.json", out _, out _);
        window.ShowPanelTab(comments: true);

        window.Run(AppCommand.EditDescription);

        var context = window.NewContext(null);
        Assert.False(context.ShowingComments, "the comments stayed in front");
        Assert.True(context.WritingDescription, "the markdown didn't open");
    }

    // ---- F6 and F7 -------------------------------------------------------------------------------

    [WinFormsTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void F6_brings_the_description_forward_and_leaves_the_keyboard_where_it_is(bool shut, bool inSidebar)
    {
        // So you can keep moving between tasks, or views, with the description following. Tab is
        // the way into it. From the comments, and from a shut panel, which it opens.
        using var window = Window("termyn-f6-focus.json", out _, out _, shown: true);
        var start = KeyboardStart(window, inSidebar);

        if (shut)
            window.Run(AppCommand.ToggleDescription);
        else
            window.ShowPanelTab(comments: true);

        start.Focus();
        Application.DoEvents();
        Assert.True(start.Focused, $"the {start.GetType().Name} never took the focus, so this says nothing");

        Press(start, Keys.F6);

        Assert.Equal((true, false), window.PanelTab);
        Assert.True(start.Focused, $"the keyboard left the {start.GetType().Name}, for the {Focused().GetType().Name}");
    }

    [WinFormsTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void F7_brings_the_comments_forward_and_leaves_the_keyboard_where_it_is(bool shut, bool inSidebar)
    {
        using var window = Window("termyn-f7-focus.json", out _, out _, shown: true);
        var start = KeyboardStart(window, inSidebar);

        if (shut)
            window.Run(AppCommand.ToggleDescription);

        start.Focus();
        Application.DoEvents();
        Assert.True(start.Focused, $"the {start.GetType().Name} never took the focus, so this says nothing");

        Press(start, Keys.F7);

        Assert.Equal((true, true), window.PanelTab);
        Assert.True(start.Focused, $"the keyboard left the {start.GetType().Name}, for the {Focused().GetType().Name}");
    }

    /// <summary>Where the keyboard is when F6 or F7 is pressed from outside the panel.</summary>
    /// <param name="window">The window</param>
    /// <param name="inSidebar">True for the sidebar, false for the task list</param>
    /// <returns>The sidebar's tree or the outline</returns>
    private static Control KeyboardStart(MainForm window, bool inSidebar) =>
        inSidebar ? TestWindow.Find<SidebarTree>(window) : TestWindow.Find<OutlineView>(window);

    [WinFormsTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_view_menus_description_and_comments_leave_the_keyboard_in_the_list(bool comments)
    {
        // The same commands as F6 and F7, clicked in the menu bar rather than pressed.
        using var window = Window("termyn-view-menu-focus.json", out _, out _, shown: true);
        var outline = TestWindow.Find<OutlineView>(window);

        if (!comments)
            window.ShowPanelTab(comments: true);

        outline.Focus();
        Application.DoEvents();
        Assert.True(outline.Focused, "the outline never took the focus, so this says nothing");

        // Opened first, as a click on the menu bar does, since that's when its entries are lit for
        // what's on screen now.
        var command = comments ? AppCommand.ViewComments : AppCommand.ViewDescription;
        var label = Termyn.Presentation.Commands.StateOf(command, window.NewContext(null)).Label;
        var heading = window.MainMenuStrip!.Items.OfType<ToolStripMenuItem>()
            .Single(menu => MenuItems(menu.DropDownItems).Any(item => item.Text == label));

        heading.ShowDropDown();
        Application.DoEvents();

        var entry = MenuItems(heading.DropDownItems).Single(item => item.Text == label);
        Assert.True(entry.Enabled, $"{label} is greyed, so this says nothing");

        entry.PerformClick();
        Application.DoEvents();

        Assert.Equal((true, comments), window.PanelTab);
        Assert.True(outline.Focused, $"the keyboard left the list, for the {Focused().GetType().Name}");
    }

    /// <summary>Every entry in a menu, and in the menus under it.</summary>
    /// <param name="items">The menu's items</param>
    /// <returns>Each entry, a heading before what's under it</returns>
    private static IEnumerable<ToolStripMenuItem> MenuItems(ToolStripItemCollection items) =>
        items.OfType<ToolStripMenuItem>().SelectMany(item => MenuItems(item.DropDownItems).Prepend(item));

    [WinFormsTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_keyboard_in_the_tab_going_behind_goes_to_the_one_coming_forward(bool toComments)
    {
        // Left where it was, it'd be on something no longer showing.
        using var window = Window("termyn-f6-f7-follow.json", out var view, out _, shown: true);
        var comments = TestWindow.Find<CommentsView>(window);

        if (toComments)
        {
            view.Focus();
            Application.DoEvents();
            Assert.True(view.Focused, "the rendering never took the focus, so this says nothing");
        }
        else
        {
            window.ShowPanelTab(comments: true);
            comments.Focus();
            Application.DoEvents();
            Assert.True(comments.ContainsFocus, "the comments never took the focus, so this says nothing");
        }

        Press(Focused(), toComments ? Keys.F7 : Keys.F6);

        Assert.Equal((true, toComments), window.PanelTab);
        Assert.True(toComments ? comments.ContainsFocus : view.Focused, "the keyboard was left behind");
    }

    [WinFormsFact]
    public void Clicking_the_comments_tab_takes_the_keyboard_there()
    {
        // Ready to type one. Unlike F7: the click has already taken the keyboard off the list, onto
        // the strip of tabs, which is no use to anyone. A click rather than setting the tab from
        // here, because setting it from code moves the keyboard into the page by itself.
        using var window = Window("termyn-comments-tab-focus.json", out _, out _, shown: true);
        var outline = TestWindow.Find<OutlineView>(window);
        var comments = TestWindow.Find<CommentsView>(window);
        var tabs = TestWindow.Find<TabControl>(window);

        outline.Focus();
        Application.DoEvents();
        Assert.True(outline.Focused, "the outline never took the focus, so this says nothing");

        var page = tabs.TabPages.IndexOf(tabs.TabPages.Cast<TabPage>().Single(p => p.Contains(comments)));
        var middle = tabs.GetTabRect(page);
        var at = (middle.Y + middle.Height / 2) << 16 | (middle.X + middle.Width / 2);

        PostMessage(tabs.Handle, WmLButtonDown, MkLButton, at);
        Application.DoEvents();
        PostMessage(tabs.Handle, WmLButtonUp, 0, at);
        Application.DoEvents();

        Assert.Equal((true, true), window.PanelTab);
        Assert.True(comments.ContainsFocus, $"the keyboard didn't go to the comments, but to the {Focused().GetType().Name}");
    }

    [WinFormsFact]
    public void Return_in_the_rendering_after_F6_and_Tab_opens_the_markdown()
    {
        using var window = Window("termyn-f6-return.json", out var view, out var editor, shown: true);
        var outline = TestWindow.Find<OutlineView>(window);
        window.ShowPanelTab(comments: true);
        outline.Focus();
        Application.DoEvents();

        Press(outline, Keys.F6);
        Press(outline, Keys.Tab);
        Assert.True(view.Focused, "F6 and Tab didn't take the keyboard to the rendering, so this says nothing");

        Press(view, Keys.Return);

        Assert.True(Writing(window), "Return in the rendering didn't open the markdown");
        Assert.True(editor.Focused, "the keyboard didn't go to the markdown");
    }

    // ---- Return on a task ------------------------------------------------------------------------

    [WinFormsTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Return_on_a_task_with_the_panel_open_starts_writing_its_description(bool fromComments)
    {
        using var window = Window("termyn-return-writes.json", out _, out var editor, shown: true);
        var outline = TestWindow.Find<OutlineView>(window);

        if (fromComments)
            window.ShowPanelTab(comments: true);

        outline.Focus();
        Application.DoEvents();

        Press(outline, Keys.Return);

        var context = window.NewContext(null);
        Assert.False(context.ShowingComments, "the comments stayed in front");
        Assert.True(context.WritingDescription, "the markdown didn't open");
        Assert.True(editor.Focused, "the keyboard didn't go to the markdown");
    }

    [WinFormsFact]
    public void Return_on_a_task_with_the_panel_shut_does_nothing()
    {
        // Return mustn't reopen a panel somebody shut.
        using var window = Window("termyn-return-shut.json", out _, out _);
        window.Run(AppCommand.ToggleDescription);

        Press(TestWindow.Find<OutlineView>(window), Keys.Return);

        var context = window.NewContext(null);
        Assert.False(context.ShowingDescription, "the panel opened");
        Assert.False(context.WritingDescription);
    }

    [WinFormsFact]
    public void The_menu_entry_is_lit_over_a_description_that_can_be_written_to_until_it_is_being_written()
    {
        using var window = Window("termyn-edit-entry-lit.json", out _, out _);

        Assert.True(Commands.StateOf(AppCommand.EditDescription, window.NewContext(null)).Enabled);

        window.Run(AppCommand.EditDescription);

        Assert.False(Commands.StateOf(AppCommand.EditDescription, window.NewContext(null)).Enabled);
    }

    // ---- The pencil ------------------------------------------------------------------------------

    [WinFormsFact]
    public void The_pencil_switches_the_description_and_stays_pressed_while_its_markdown_shows()
    {
        using var window = Window("termyn-pencil.json", out _, out _);
        var pencil = Pencil(window);

        Assert.True(pencil.Enabled, "the pencil isn't on offer for a description that can be written to");
        Assert.False(pencil.Checked, "the pencil is pressed over the rendering");

        pencil.PerformClick();
        Assert.True(Writing(window), "the pencil didn't open the markdown");
        Assert.True(pencil.Checked, "the pencil isn't pressed over the markdown");

        pencil.PerformClick();
        Assert.False(Writing(window), "the pencil didn't close the markdown");
        Assert.False(pencil.Checked);
    }

    [WinFormsFact]
    public void The_pencil_is_named_for_what_a_click_would_do_with_the_key_that_does_the_same()
    {
        // The name's what a screen reader reads; the tooltip adds the key a picture can't show.
        using var window = Window("termyn-pencil-name.json", out _, out _);
        var pencil = Pencil(window);

        Assert.Equal("Edit description", pencil.Text);
        Assert.Equal("Edit description (Ctrl+E)", pencil.ToolTipText);

        pencil.PerformClick();

        Assert.Equal("Preview description", pencil.Text);
        Assert.Equal("Preview description (Esc)", pencil.ToolTipText);
    }

    [WinFormsFact]
    public void The_pencil_is_greyed_while_there_is_nothing_to_write_to()
    {
        // Nothing is selected when the window opens, so there's nowhere for typing to go.
        using var window = TestWindow.Build("termyn-pencil-greyed.json");
        _ = window.Handle;

        Assert.False(Pencil(window).Enabled);
    }

    [WinFormsFact]
    public async Task The_pencil_greys_when_the_description_stops_taking_typing_with_the_same_task_selected()
    {
        // The token turned away while a task's description is being read: the selection hasn't
        // moved, so nothing switches the panel between reading and writing, and only being told
        // about the change of access greys the pencil.
        var store = new InMemorySnapshotStore();
        store.PutResource(ResourceType.User, ResourceType.User, """{"id":"u1"}""");
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("items", "t1", """{"id":"t1","content":"Plan the week","description":"Notes","project_id":"p","child_order":1}""");
        var api = new FakeApi();
        var settings = Path.Combine(Path.GetTempPath(), $"termyn-pencil-greyed-on-reject-{Guid.NewGuid():N}.json");

        try
        {
            using var window = TestWindow.Start(settings, store, api, out var presenter);
            TestWindow.ShowOffScreen(window);
            presenter.Select(ViewSelection.OfProject("p"));
            window.ShowPanelTab(comments: false);

            var outline = TestWindow.Find<OutlineView>(window);
            _ = outline.Handle;
            outline.SelectId("t1");

            var pencil = Pencil(window);
            Assert.True(pencil.Enabled, "the pencil isn't on offer to begin with, so this says nothing");

            api.Throw = new TodoistAuthException("rejected");
            try
            {
                await presenter.SyncAsync();
            }
            catch (TodoistAuthException)
            {
                // What a rejected token is reported as; the engine has let go of it either way.
            }

            Application.DoEvents();

            Assert.True(presenter.ReconnectNeeded);
            Assert.True(TestWindow.Find<MarkdownEditor>(window).ReadOnly, "the window never heard, so this says nothing");
            Assert.False(pencil.Enabled);
        }
        finally
        {
            File.Delete(settings);
        }
    }

    [WinFormsTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_pressed_pencil_has_a_solid_edge_in_the_accent_and_the_other_has_none(bool writing)
    {
        // In the dark theme, where the fill a pressed pencil has is too close to the panel to see,
        // and the edge is all there is to say it's on.
        var theme = Theme.Resolve(ThemePreference.Dark);
        using var bar = new DescriptionBar { Width = 300, Theme = theme };
        bar.CreateControl();
        bar.Render(writing, writable: true);

        using var shot = new Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(shot, new Rectangle(0, 0, bar.Width, bar.Height));

        var pencil = bar.Pencil.Bounds;
        var edge = Color.FromArgb(255, shot.GetPixel(bar.Tools.Left + pencil.Left, bar.Tools.Top + pencil.Top + (pencil.Height / 2)));

        if (writing)
            Assert.Equal(theme.Accent, edge);
        else
            Assert.NotEqual(theme.Accent, edge);
    }

    private static ToolStripButton Pencil(MainForm window) => TestWindow.Find<DescriptionBar>(window).Pencil;

    // ---- The ring --------------------------------------------------------------------------------

    [WinFormsFact]
    public void The_ring_is_round_the_markdown_with_the_keyboard_and_not_round_the_rendering()
    {
        // Typing at the rendering changes nothing, so a ring there would promise what doesn't happen.
        using var window = Window("termyn-ring-in-window.json", out var view, out var editor, shown: true);
        var frame = TestWindow.Find<FocusFrame>(window);

        view.Focus();
        Application.DoEvents();
        Assert.True(view.Focused, "the rendering never took the focus, so this says nothing");
        Assert.False(frame.Lit, "the ring is round the rendering");

        window.Run(AppCommand.EditDescription);
        Application.DoEvents();
        Assert.True(editor.Focused, "the markdown never took the focus, so this says nothing");
        Assert.True(frame.Lit, "the ring isn't round the markdown");
    }

    [WinFormsTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void The_ring_is_drawn_round_a_focused_box_only_when_it_takes_typing(bool takesTyping, bool lit)
    {
        var theme = Theme.Resolve(ThemePreference.Light);

        using var form = new Form { Size = new Size(300, 200) };
        var frame = new FocusFrame { Theme = theme, TakesTyping = takesTyping };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true };
        frame.Controls.Add(box);
        form.Controls.Add(frame);

        TestWindow.ShowOffScreen(form);
        box.Focus();
        Application.DoEvents();

        Assert.True(box.Focused, "the box never took the focus, so this says nothing");
        Assert.Equal(lit ? theme.Accent : theme.Panel, Edge(frame));
    }

    [WinFormsFact]
    public void The_ring_goes_when_the_focus_leaves()
    {
        var theme = Theme.Resolve(ThemePreference.Light);

        using var form = new Form { Size = new Size(300, 200) };
        var frame = new FocusFrame { Theme = theme, TakesTyping = true };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true };
        var elsewhere = new TextBox { Dock = DockStyle.Bottom };
        frame.Controls.Add(box);
        form.Controls.Add(frame);
        form.Controls.Add(elsewhere);

        TestWindow.ShowOffScreen(form);
        box.Focus();
        Application.DoEvents();
        Assert.Equal(theme.Accent, Edge(frame));

        elsewhere.Focus();
        Application.DoEvents();

        Assert.Equal(theme.Panel, Edge(frame));
    }

    /// <summary>A pixel on the frame's left edge, where the ring is drawn when it's drawn.</summary>
    private static Color Edge(FocusFrame frame)
    {
        using var shot = new Bitmap(frame.Width, frame.Height);
        frame.DrawToBitmap(shot, new Rectangle(0, 0, frame.Width, frame.Height));
        return Color.FromArgb(255, shot.GetPixel(0, frame.Height / 2));
    }

    // ---- Keys ------------------------------------------------------------------------------------

    /// <summary>
    /// Presses a key with its modifiers held, through the message loop the way a keyboard does, so
    /// the window's own key handling sees it first.
    /// </summary>
    private static void Press(Control box, Keys keys)
    {
        var code = keys & Keys.KeyCode;
        var state = new byte[256];

        if (keys.HasFlag(Keys.Control))
            state[0x11] = state[0xA2] = 0x80;
        if (keys.HasFlag(Keys.Shift))
            state[0x10] = state[0xA0] = 0x80;

        try
        {
            state[(int)code] = 0x80;
            Hold(state);
            PostMessage(box.Handle, WmKeyDown, (nint)code, 1);
            Application.DoEvents();

            state[(int)code] = 0;
            Hold(state);
            PostMessage(box.Handle, WmKeyUp, (nint)code, unchecked((int)0xC0000001));
            Application.DoEvents();
        }
        finally
        {
            Hold(new byte[256]);
        }
    }

    /// <summary>
    /// Sets which keys this thread takes to be held, after taking any change the real keyboard still
    /// has on its way, so a key held in another window can't get in.
    /// </summary>
    private static void Hold(byte[] state)
    {
        GetKeyState(VkShift);
        SetKeyboardState(state);
    }
}
