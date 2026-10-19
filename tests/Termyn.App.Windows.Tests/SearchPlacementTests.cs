namespace Termyn.App.Windows.Tests;

/// <summary>
/// Where the search box sits in the window: in from its edges rather than running into them.
/// </summary>
public class SearchPlacementTests
{
    [WinFormsFact]
    public void The_search_box_sits_in_from_the_sides_of_the_window()
    {
        using var window = TestWindow.Build("termyn-search-placement.json");
        var box = InWindow(TestWindow.Find<SearchBox>(window), window);

        Assert.Equal(MainForm.SearchPadding.Left, box.Left);
        Assert.Equal(MainForm.SearchPadding.Right, window.ClientSize.Width - box.Right);
    }

    [WinFormsFact]
    public void There_is_room_above_and_below_it_without_cutting_it_short()
    {
        using var window = TestWindow.Build("termyn-search-placement.json");
        var search = TestWindow.Find<SearchBox>(window);
        var box = InWindow(search, window);
        var bar = window.MainMenuStrip!;
        var below = window.Controls.OfType<SplitContainer>().Single();

        Assert.Equal(MainForm.SearchPadding.Top, box.Top - bar.Bottom);
        Assert.Equal(MainForm.SearchPadding.Bottom, below.Top - box.Bottom);
        Assert.Equal(search.PreferredHeight, box.Height);
    }

    /// <summary>
    /// A control's bounds in the window's client area, however deep inside it the control sits.
    /// </summary>
    private static Rectangle InWindow(Control control, Form window)
    {
        var bounds = control.Bounds;
        for (var parent = control.Parent; parent is not null && parent != window; parent = parent.Parent)
            bounds.Offset(parent.Location);

        return bounds;
    }
}
