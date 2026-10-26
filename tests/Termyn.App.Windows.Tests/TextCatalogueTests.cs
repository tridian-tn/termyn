using System.Globalization;
using Termyn.Presentation;
using Termyn.TestSupport;

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
    [WinFormsTheory]
    [InlineData(1, "Termyn — 1 task due today")]
    [InlineData(3, "Termyn — 3 tasks due today")]
    [InlineData(21, "Termyn — 21 tasks due today")]
    public void The_tray_counts_todays_tasks_in_the_form_the_count_takes(int due, string expected)
        => Assert.Equal(expected, Plurals.Format(Strings.ResourceManager, nameof(Strings.TrayDue_Other), due));

    [WinFormsFact]
    public void The_catalogue_names_a_language_its_counts_have_a_rule_for()
        => Assert.True(Plurals.HasRuleFor(Strings.CatalogueLanguage), Strings.CatalogueLanguage);

    [WinFormsFact]
    public void Every_form_of_a_counted_phrase_has_an_Other_beside_it_and_shows_its_count()
    {
        // _Other is what every count falls back to, so a phrase without one has nothing to say for a
        // form a translation leaves out. And {0} is in every form, _One included: in some languages
        // "one" covers 21 as well as 1.
        var entries = Source.Value.Entries;
        var forms = entries.Where(e => CatalogueSource.IsCountedForm(e.Name)).ToList();

        Assert.NotEmpty(forms);
        Assert.Empty(forms.Where(e => !entries.Any(other => other.Name == CatalogueSource.OtherFormOf(e.Name))).Select(e => e.Name));
        Assert.Empty(forms.Where(e => !e.Value.Contains("{0}", StringComparison.Ordinal)).Select(e => e.Name));
    }

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

    [WinFormsFact]
    public void Nothing_in_the_catalogue_is_protocol()
        => Assert.Empty(ProtocolText.InCataloguesOf(typeof(Strings).Assembly));

    [WinFormsFact]
    public void Every_entry_tells_a_translator_where_it_appears()
        => Assert.Empty(Source.Value.Bare());

    [WinFormsFact]
    public void Every_placeholder_is_explained()
        => Assert.Empty(Source.Value.Unexplained());

    [WinFormsFact]
    public void Every_entry_is_used()
        => Assert.Empty(Source.Value.Unused(Plurals.LanguageEntry));

    /// <summary>
    /// The catalogue as it's written, and the code that reads it. Read when a test first asks, so
    /// a source tree that can't be found fails the tests that read it and not the rest of this class.
    /// </summary>
    private static readonly Lazy<CatalogueSource> Source = new(() => new CatalogueSource("Termyn.App.Windows"));
}
