using Termyn.Core.Settings;

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
    private static readonly Theme Light = Theme.Resolve(ThemePreference.Light);

    private static SearchBox Box(Theme? theme = null)
    {
        var box = new SearchBox { Theme = theme ?? Light };
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

    [Theory]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public void The_cross_sits_on_the_box_rather_than_on_the_window(ThemePreference preference)
    {
        // Applying a theme walks every control it can reach and paints a label the window's
        // background, which is not the box's. Left at that the cross sat on a strip of the wrong
        // colour — invisible in the light theme, and not in the dark one.
        var theme = Theme.Resolve(preference);
        using var box = Box(theme);
        box.Text = "milk";

        theme.Apply(box);
        box.Theme = theme;

        Assert.Equal(box.BackColor, box.ResetBackColour);
    }

    [Theory]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public void The_cross_lights_under_the_pointer_and_goes_quiet_again(ThemePreference preference)
    {
        // Quiet is the whole reason it was hard to see, and lighting it is the answer that doesn't
        // shout at rest. Both colours come from the palette, so this holds in either theme.
        var theme = Theme.Resolve(preference);
        using var box = Box(theme);
        box.Text = "milk";

        Assert.Equal(theme.Muted, box.ResetColour);
        Assert.Equal(box.BackColor, box.ResetBackColour);

        box.Highlight(true);

        Assert.Equal(theme.Text, box.ResetColour);
        Assert.Equal(theme.Row, box.ResetBackColour);

        box.Highlight(false);

        Assert.Equal(theme.Muted, box.ResetColour);
        Assert.Equal(box.BackColor, box.ResetBackColour);
    }

    [Fact]
    public void A_cross_left_lit_comes_back_quiet()
    {
        // The pointer never leaves a control that vanishes under it, so nothing would put it back.
        using var box = Box();
        box.Text = "milk";
        box.Highlight(true);

        box.Reset();
        box.Text = "bread";

        Assert.Equal(Light.Muted, box.ResetColour);
    }

    [Fact]
    public void The_cross_is_a_glyph_this_knows_it_can_draw()
    {
        // Which of the two it lands on is a fact about the machine rather than about the code, so
        // this holds the choice rather than the outcome: whatever font is or isn't installed, what
        // comes out is one of the two glyphs and never the empty box a missing font would give.
        using var box = Box();
        var (native, plain) = SearchBox.Glyphs;

        Assert.Contains(box.ResetGlyph, new[] { native, plain });
    }

    /// <summary>WM_KEYDOWN, and the virtual key for Escape.</summary>
    private const int WmKeyDown = 0x0100;

    /// <summary>Presses a key at the box the way the message loop would, so the binding runs.</summary>
    private static bool Press(SearchBox box, Keys key)
    {
        var message = Message.Create(box.Handle, WmKeyDown, (nint)key, 0);
        return box.PreProcessMessage(ref message);
    }

    [Fact]
    public void Escape_empties_the_box()
    {
        using var box = Box();
        box.Text = "milk";

        Assert.True(Press(box, Keys.Escape));

        Assert.Equal(string.Empty, box.Text);
        Assert.False(box.ShowingReset);
    }

    [Fact]
    public void Escape_in_an_empty_box_is_left_for_something_else()
    {
        // Nothing else in the window wants it while this has the focus, but a key swallowed by
        // whatever happens to be focused is the sort of thing nobody can account for later.
        using var box = Box();

        Assert.False(box.TakesEscape);
        Assert.False(Press(box, Keys.Escape));
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
