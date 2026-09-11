
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
        // hands its first panel over before its second.
        using var window = Window();

        // By what each one is rather than by the name of its class, which said TextBox until the
        // search box became one of its own and broke this for a reason that had nothing to do with
        // the order.
        Assert.Collection(
            Order(window).Where(c => c.TabStop),
            first => Assert.True(first is TextBox, $"the search box should come first, not {first.GetType().Name}"),
            then => Assert.True(then is SplitContainer, $"the tree and list should come next, not {then.GetType().Name}"));
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

    private static MainForm Window() => TestWindow.Build("termyn-tab-order.json");
}
