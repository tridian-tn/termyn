namespace Termyn.App.Windows.Tests;

/// <summary>
/// The search box's cross, realised without ever being shown.
/// </summary>
/// <remarks>
/// That clicking it clears the box is not asserted here: the cross is the control's own business
/// and a test reaching in to press it would be asserting the wiring rather than the behaviour. The
/// click calls <see cref="SearchBox.Reset"/> and nothing else, so what is worth holding is what
/// Reset does and when the cross is offered at all.
/// </remarks>
public class SearchBoxTests
{
    private static SearchBox Box()
    {
        var box = new SearchBox();
        box.CreateControl();
        return box;
    }

    [Fact]
    public void There_is_nothing_to_clear_until_something_is_typed()
    {
        using var box = Box();

        Assert.False(box.ShowingReset);

        box.Text = "milk";
        Assert.True(box.ShowingReset);
    }

    [Fact]
    public void Clearing_it_takes_the_cross_away_with_the_words()
    {
        using var box = Box();
        box.Text = "milk";

        box.Reset();

        Assert.Equal(string.Empty, box.Text);
        Assert.False(box.ShowingReset);
    }

    [Fact]
    public void Emptying_it_by_hand_takes_the_cross_away_too()
    {
        // The cross follows what is in the box rather than how it came to be empty, so selecting
        // the lot and deleting it leaves nothing offering to clear nothing.
        using var box = Box();
        box.Text = "milk";

        box.Text = string.Empty;

        Assert.False(box.ShowingReset);
    }

    [Fact]
    public void Whitespace_is_something_to_clear()
    {
        // A box of spaces isn't a search — the outline shows the view for one — but it is not an
        // empty box either, and the way back to one is the same cross.
        using var box = Box();

        box.Text = "   ";

        Assert.True(box.ShowingReset);
    }
}
