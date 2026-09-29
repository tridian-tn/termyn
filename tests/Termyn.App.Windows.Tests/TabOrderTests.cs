using System.Runtime.InteropServices;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Where Tab goes in the main window.
/// </summary>
/// <remarks>
/// The first thing in this suite to build a whole window. Everything else here works a control at
/// a time, which is why the window's own wiring has gone untested — and why a write that forgot to
/// wake the sync loop could sit in it unnoticed. The shell it needs is eight interfaces, and the
/// record that carries them always said tests could stand in for them.
/// </remarks>
public class TabOrderTests
{
    [WinFormsFact]
    public void Tab_starts_at_the_search_box_and_then_goes_down_the_window()
    {
        // Order comes from TabIndex, and TabIndex falls out of the order controls were added in
        // unless it is said — which for this window is the order docking wanted, not the order a
        // hand moving down it wants. Tab used to start at the split and reach the search box third.
        //
        // These are the window's own children. What is inside the split — the tree, then the list
        // and the panel — follows from the nesting rather than from anything set here: a split
        // hands its first panel over before its second. All of them rather than only the stops,
        // since the split isn't one itself any more, just the way to what's inside it.
        using var window = Window();

        // By what each one is rather than by the name of its class, which said TextBox until the
        // search box became one of its own and broke this for a reason that had nothing to do with
        // the order.
        Assert.Collection(
            Order(window).Where(c => c.Parent == window).Take(2),
            first => Assert.True(first is TextBox, $"the search box should come first, not {first.GetType().Name}"),
            then => Assert.True(then is SplitContainer, $"the tree and list should come next, not {then.GetType().Name}"));
    }

    [WinFormsTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Tab_goes_from_the_search_box_to_the_tree_the_list_and_the_panel(bool comments, bool forward)
    {
        // The dividers between them and the strip naming the panel's tabs used to be stops as
        // well, so getting from the tree to the list took two presses and into the panel three.
        // A divider's dragged with the mouse, and F6 and F7 pick the tab. So was the path above
        // the list, in a view where it has a parent project to link to.
        using var window = Shown(comments);
        var round = Round(window, forward);

        // Shift+Tab is read back to front, so both ways are held to the same order.
        List<Control> stops = forward ? round : [round[0], .. round.Skip(1).Reverse()];

        Assert.Collection(
            stops.Take(3),
            first => Assert.True(first is TextBox, $"the search box should come first, not {first.GetType().Name}"),
            then => Assert.True(then is TreeView, $"the tree should come next, not {then.GetType().Name}"),
            then => Assert.True(then is ListView, $"the list should come after the tree, not {then.GetType().Name}"));

        // And then only what the tab in front shows, which the dividers, the strip and the path
        // aren't part of.
        var page = TestWindow.Find<TabControl>(window).SelectedTab!;
        Assert.NotEmpty(stops.Skip(3));
        Assert.All(stops.Skip(3), stop => Assert.True(page.Contains(stop), $"{stop.GetType().Name} isn't part of what the tab shows"));
    }

    [WinFormsFact]
    public void The_status_line_and_the_menu_bar_are_not_stops_on_the_way()
    {
        // Neither is somewhere a caret goes. The status is a label — the menu has its own key.
        using var window = Window();

        Assert.DoesNotContain(Order(window).Where(c => c.TabStop), c => c is Label or MenuStrip);
    }

    /// <summary>The window's children, in the order Tab visits them.</summary>
    private static List<Control> Order(Form window)
    {
        var order = new List<Control>();

        Control? at = null;
        while ((at = window.GetNextControl(at, forward: true)) is not null)
            order.Add(at);

        return order;
    }

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    private const int WmKeyDown = 0x0100;
    private const int VkShift = 0x10;

    /// <summary>
    /// Presses Tab from the search box until the focus comes back to it.
    /// </summary>
    /// <remarks>
    /// Each press goes through <c>PreProcessMessage</c> on whatever has the focus, which is where
    /// the message loop hands a key before the control sees it. So every container on the way out
    /// gets its say in where the focus goes, the way it does for a real press. Asking the window
    /// for its next control doesn't: it stops at a split's edge and never looks inside.
    ///
    /// Shift is set on this thread for each press rather than left to the machine. The key goes
    /// out with whatever modifiers the thread thinks are down, and this thread's key state follows
    /// the real keyboard — a Shift held in another window would turn Tab round. It's asked about
    /// before it's set, since a change still on its way lands on the next question over the top
    /// of anything set in between.
    /// </remarks>
    /// <param name="window">The window, shown</param>
    /// <param name="forward">True for Tab, false for Shift+Tab</param>
    /// <returns>Each control the focus landed on, starting with the search box</returns>
    private static List<Control> Round(Form window, bool forward)
    {
        var search = TestWindow.Find<SearchBox>(window);
        search.Focus();
        Assert.True(search.Focused, "The search box wouldn't take the focus, so there's nowhere to start from.");

        var held = new byte[256];
        if (!forward)
            held[VkShift] = 0x80;

        var stops = new List<Control> { search };
        try
        {
            // Far more presses than the window has stops, so a round that never gets back to the
            // search box fails rather than going on for ever.
            for (var press = 0; press < 20; press++)
            {
                var at = Control.FromChildHandle(GetFocus());
                Assert.NotNull(at);

                GetKeyState(VkShift);
                SetKeyboardState(held);

                var message = Message.Create(at.Handle, WmKeyDown, (nint)Keys.Tab, 0);
                at.PreProcessMessage(ref message);

                var next = Control.FromChildHandle(GetFocus());
                Assert.NotNull(next);

                if (next == search)
                    return stops;

                stops.Add(next);
            }
        }
        finally
        {
            GetKeyState(VkShift);
            SetKeyboardState(new byte[256]);
        }

        Assert.Fail($"Tab never came back to the search box: {string.Join(", ", stops.Select(s => s.GetType().Name))}");
        return stops;
    }

    private static MainForm Window() => TestWindow.Build("termyn-tab-order.json");

    /// <summary>
    /// A window on a task in a sub-project, with the panel open on one of its tabs, shown so its
    /// controls can take the focus.
    /// </summary>
    /// <remarks>
    /// A sub-project so the path above the list has a parent to link to, which is when it could be
    /// a stop. The task has a description, so the panel opens on the rendering the way it does for
    /// most tasks — not on an empty one ready to write, which changes to reading as the focus
    /// leaves it.
    /// </remarks>
    /// <param name="comments">True to open the panel on the comments, false for the description</param>
    /// <returns>The window, shown off-screen, which the caller disposes</returns>
    private static MainForm Shown(bool comments)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Launch","parent_id":"p1","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"Ship it","description":"Notes","project_id":"p2","child_order":1}""");

        var window = TestWindow.Build("termyn-tab-round.json", store, out _, out var presenter);

        // A control on a window nobody has shown can't take the focus.
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-2000, -2000);
        window.Show();

        presenter.Select(ViewSelection.OfProject("p2"));
        TestWindow.Find<OutlineView>(window).SelectId("a");
        window.ShowPanelTab(comments);

        // Without a link on the path there'd be nothing to stop on, and the order would pass
        // whether the path was out of it or not.
        Assert.Contains(TestWindow.Descendants(window).OfType<LinkLabel>(), l => l.Text.StartsWith("Work") && l.Links.Count > 0);

        return window;
    }
}
