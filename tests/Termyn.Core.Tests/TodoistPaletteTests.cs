using Termyn.Core.Settings;

namespace Termyn.Core.Tests;

/// <summary>
/// Todoist's own colours, which it names rather than gives.
/// </summary>
/// <remarks>
/// The values are from Todoist's API reference, which numbers the same twenty 30–49. Payloads have
/// carried both forms, so both are read, and anything else has to come back as something rather
/// than as nothing.
/// </remarks>
public class TodoistPaletteTests
{
    [Theory]
    [InlineData("berry_red", "#B8255F")]
    [InlineData("red", "#DC4C3E")]
    [InlineData("blue", "#4180FF")]
    [InlineData("charcoal", "#808080")]
    [InlineData("taupe", "#8F7A69")]
    public void A_colour_is_what_todoist_says_it_is(string name, string expected)
        => Assert.Equal(expected, TodoistPalette.Of(name).ToString());

    [Theory]
    [InlineData("30", "#B8255F")]   // the first Todoist numbers
    [InlineData("41", "#4180FF")]   // blue, twelfth in the list
    [InlineData("49", "#8F7A69")]   // taupe, the last
    public void The_numbers_older_payloads_use_mean_the_same_colours(string id, string expected)
        => Assert.Equal(expected, TodoistPalette.Of(id).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("29")]              // below the numbering
    [InlineData("50")]              // above it
    [InlineData("puce")]            // a colour Todoist might add later
    public void Anything_it_does_not_recognise_is_charcoal(string? colour)
    {
        // Charcoal is what Todoist itself gives something with no colour of its own, so a colour we
        // haven't met shows as grey rather than as nothing at all.
        Assert.Equal(TodoistPalette.Charcoal, TodoistPalette.Of(colour));
    }

    [Fact]
    public void A_name_is_read_however_it_is_cased_or_spaced()
        => Assert.Equal(TodoistPalette.Of("berry_red"), TodoistPalette.Of("  Berry_Red "));

    [Fact]
    public void All_twenty_are_there_and_none_is_repeated()
    {
        Assert.Equal(20, TodoistPalette.Names.Count);

        var drawn = TodoistPalette.Names.Select(n => TodoistPalette.Of(n).ToString()).ToList();

        Assert.Equal(drawn.Count, drawn.Distinct().Count());
    }
}
