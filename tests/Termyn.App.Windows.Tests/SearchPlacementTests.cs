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

        Assert.Equal(MainForm.SearchPaddingAt(window.DeviceDpi).Left, box.Left);
        Assert.Equal(MainForm.SearchPaddingAt(window.DeviceDpi).Right, window.ClientSize.Width - box.Right);
    }

    [WinFormsFact]
    public void There_is_room_above_and_below_it_without_cutting_it_short()
    {
        using var window = TestWindow.Build("termyn-search-placement.json");
        var search = TestWindow.Find<SearchBox>(window);
        var box = InWindow(search, window);
        var bar = window.MainMenuStrip!;
        var below = window.Controls.OfType<SplitContainer>().Single();

        Assert.Equal(MainForm.SearchPaddingAt(window.DeviceDpi).Top, box.Top - bar.Bottom);
        Assert.Equal(MainForm.SearchPaddingAt(window.DeviceDpi).Bottom, below.Top - box.Bottom);
        Assert.Equal(search.PreferredHeight, box.Height);
    }

    [WinFormsFact]
    public void The_space_round_it_grows_with_the_screen_s_scale()
    {
        // The sidebar's text lead is scaled, and the box is meant to line up with it, so a box
        // padded in raw pixels drifts out of line at anything but 100%.
        Assert.Equal(MainForm.SearchPadding, MainForm.SearchPaddingAt(96));
        Assert.Equal(new Padding(6, 9, 6, 9), MainForm.SearchPaddingAt(144));
        Assert.Equal(new Padding(8, 12, 8, 12), MainForm.SearchPaddingAt(192));
    }

    [WinFormsFact]
    public void A_search_waits_for_the_typing_to_pause_and_a_sync_meanwhile_leaves_the_box_alone()
    {
        // Every keystroke used to render the whole outline. Held back now, the box is a letter or
        // two ahead of the presenter for a moment, and a render in that moment mustn't take them off.
        using var window = TestWindow.Build("termyn-search-pause.json", null, out _, out var presenter);
        TestWindow.ShowOffScreen(window);
        var search = TestWindow.Find<SearchBox>(window);

        search.Text = "ship";

        Assert.True(window.SearchPending);
        Assert.Equal(string.Empty, presenter.SearchQuery);

        // The fake server answers at once, so this has rendered by the time it returns.
        _ = presenter.SyncAsync();
        Assert.Equal("ship", search.Text);

        // Emptying the box isn't typing, so it goes at once.
        search.Text = string.Empty;
        Assert.False(window.SearchPending);
        Assert.Equal(string.Empty, presenter.SearchQuery);
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
