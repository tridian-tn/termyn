using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The box that keeps its hint up while it's being typed into, realised without ever being shown.
/// </summary>
/// <remarks>
/// That the hint survives the focus is the reason the control exists, and it isn't asserted here:
/// a control on no visible form never takes the focus, so a test that called <c>Focus</c> would be
/// checking the unfocused case twice and reading as though it had done more. What can be said is
/// that nothing in the decision consults the focus, and these hold it to the one thing it does.
/// </remarks>
public class HintTextBoxTests
{
    private static HintTextBox Box()
    {
        var box = new HintTextBox { Hint = CapturePreviewText.Hint };
        box.CreateControl();
        return box;
    }

    [Fact]
    public void The_hint_is_up_while_the_box_is_empty_and_gone_once_it_is_not()
    {
        using var box = Box();

        Assert.True(box.ShowingHint);

        box.Text = "Email the report";
        Assert.False(box.ShowingHint);

        box.Clear();
        Assert.True(box.ShowingHint);
    }

    [Fact]
    public void A_box_with_no_hint_to_show_shows_nothing()
    {
        using var box = new HintTextBox();
        box.CreateControl();

        Assert.False(box.ShowingHint);
    }
}
