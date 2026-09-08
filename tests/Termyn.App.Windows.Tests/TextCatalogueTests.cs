using System.Globalization;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// That the windows' own text comes out of the catalogue.
/// </summary>
/// <remarks>
/// Read straight off the catalogue rather than off a control. What the accessor hands back is the
/// thing in doubt — a resource that isn't embedded, or is named differently from its key, gives
/// null here and an empty prompt on screen — and putting a form up to read it back would test the
/// form instead.
/// </remarks>
public class TextCatalogueTests
{
    [WinFormsFact]
    public void The_search_prompt_comes_from_the_catalogue()
        => Assert.Equal("Search…", Strings.SearchPlaceholder);

    [WinFormsFact]
    public void It_still_says_something_on_a_machine_set_to_a_language_nobody_has_translated_to()
    {
        // Not what it says, only that it says it: this has to go on passing the day a translation
        // lands rather than being one more thing to find and change.
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

            Assert.NotEmpty(Strings.SearchPlaceholder);
        }
        finally
        {
            CultureInfo.CurrentUICulture = was;
        }
    }
}
