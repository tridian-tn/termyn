
namespace Termyn.App.Windows.Tests;

/// <summary>
/// How the details panel's two tabs are asked for.
/// </summary>
/// <remarks>
/// Each names a tab rather than toggling one, so asking for the one already in front does nothing.
/// Toggling was the old behaviour, and it meant the same keystroke took you off the description as
/// often as it took you to it — which is the failure these are here to keep out.
/// </remarks>
public class PanelTabTests
{
    [WinFormsFact]
    public void Asking_for_the_description_opens_the_panel_on_it()
    {
        using var window = Window();
        Assert.False(window.PanelTab.Open);

        window.ShowPanelTab(comments: false);

        Assert.Equal((true, false), window.PanelTab);
    }

    [WinFormsFact]
    public void Asking_for_the_comments_opens_the_panel_on_them()
    {
        using var window = Window();

        window.ShowPanelTab(comments: true);

        Assert.Equal((true, true), window.PanelTab);
    }

    [WinFormsFact]
    public void Asking_again_for_the_tab_already_in_front_does_nothing()
    {
        // The whole of what was asked for. Toggling would have shut the panel here, or crossed to
        // the other tab — either way answering a question with something other than its answer.
        using var window = Window();

        window.ShowPanelTab(comments: false);
        window.ShowPanelTab(comments: false);
        Assert.Equal((true, false), window.PanelTab);

        window.ShowPanelTab(comments: true);
        window.ShowPanelTab(comments: true);
        Assert.Equal((true, true), window.PanelTab);
    }

    [WinFormsFact]
    public void Each_crosses_to_the_other()
    {
        using var window = Window();

        window.ShowPanelTab(comments: true);
        window.ShowPanelTab(comments: false);
        Assert.Equal((true, false), window.PanelTab);

        window.ShowPanelTab(comments: true);
        Assert.Equal((true, true), window.PanelTab);
    }

    private static MainForm Window() => TestWindow.Build("termyn-panel-tabs.json");
}
