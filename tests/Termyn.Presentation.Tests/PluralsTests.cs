using System.Collections;
using System.Globalization;
using System.Resources;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// That a counted phrase takes the form its count calls for, by the rule of the language it's
/// written in.
/// </summary>
public class PluralsTests
{
    [Theory]
    [InlineData(0, "0 tasks")]
    [InlineData(1, "1 task")]
    [InlineData(2, "2 tasks")]
    [InlineData(21, "21 tasks")]
    public void A_count_takes_the_form_English_gives_it(int count, string expected)
        => Assert.Equal(expected, Plurals.Format(Strings.ResourceManager, nameof(Strings.StatusTasks_Other), count));

    [Theory]
    [InlineData(1, "Synced · 1 pending · 1 failed")]
    [InlineData(3, "Synced · 3 pending · 3 failed")]
    public void A_phrase_English_says_the_same_at_any_count_has_one_form(int count, string expected)
        => Assert.Equal(expected, new SyncStatus(SyncState.Synced, Pending: count, Failed: count).Describe());

    [Fact]
    public void On_a_machine_set_to_another_language_the_English_words_keep_the_English_rule()
    {
        // No French catalogue, so the words are English, and only English's rule fits them. The
        // machine's language has no rule here and would put every count in the _Other form,
        // "1 tasks"; French's own rule would put nought in the _One form, "0 task".
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

            Assert.Equal("0 tasks", Plurals.Format(Strings.ResourceManager, nameof(Strings.StatusTasks_Other), 0));
            Assert.Equal("1 task", Plurals.Format(Strings.ResourceManager, nameof(Strings.StatusTasks_Other), 1));
        }
        finally
        {
            CultureInfo.CurrentUICulture = was;
        }
    }

    [Fact]
    public void The_catalogue_names_a_language_its_counts_have_a_rule_for()
        => Assert.True(Plurals.HasRuleFor(Strings.CatalogueLanguage), Strings.CatalogueLanguage);

    [Fact]
    public void A_language_with_more_forms_gets_the_one_its_rule_picks()
    {
        var catalogue = new Catalogue(("Apples_One", "{0} one"), ("Apples_Few", "{0} few"), ("Apples_Other", "{0} other"));

        Assert.Equal("1 one", Plurals.Format(catalogue, "Apples_Other", 1, Slavic));
        Assert.Equal("3 few", Plurals.Format(catalogue, "Apples_Other", 3, Slavic));
        Assert.Equal("5 other", Plurals.Format(catalogue, "Apples_Other", 5, Slavic));
    }

    [Fact]
    public void A_form_the_catalogue_leaves_out_falls_back_to_Other()
        => Assert.Equal("3 other", Plurals.Format(new Catalogue(("Apples_Other", "{0} other")), "Apples_Other", 3, Slavic));

    [Fact]
    public void A_phrase_is_named_by_its_Other_form()
        => Assert.Throws<ArgumentException>(() => Plurals.Format(new Catalogue(("Apples_One", "{0} one")), "Apples_One", 1, Slavic));

    [Fact]
    public void Every_form_of_a_counted_phrase_has_an_Other_beside_it_and_shows_its_count()
    {
        // _Other is what every count falls back to, so a phrase without one has nothing to say for a
        // form a translation leaves out. And {0} is in every form, _One included: in some languages
        // "one" covers 21 as well as 1.
        var entries = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var forms = entries.Where(e => CatalogueSource.IsCountedForm(e.Key)).ToList();

        Assert.NotEmpty(forms);
        Assert.Empty(forms.Where(e => !entries.ContainsKey(CatalogueSource.OtherFormOf(e.Key))).Select(e => e.Key));
        Assert.Empty(forms.Where(e => !e.Value.Contains("{0}", StringComparison.Ordinal)).Select(e => e.Key));
    }

    [Fact]
    public void The_catalogue_checks_know_a_form_by_every_name_Plurals_has_for_one()
    {
        // The checks name the forms for themselves, so a category added here and not there would
        // leave its entries unchecked.
        Assert.All(Enum.GetNames<PluralCategory>(), form => Assert.True(CatalogueSource.IsCountedForm("Apples_" + form), form));
        Assert.All(Enum.GetNames<PluralCategory>(), form => Assert.Equal("Apples_Other", CatalogueSource.OtherFormOf("Apples_" + form)));
    }

    /// <summary>A rule with a form for two to four, shaped like Polish's but simpler.</summary>
    private static PluralCategory Slavic(int n) => n switch
    {
        1 => PluralCategory.One,
        >= 2 and <= 4 => PluralCategory.Few,
        _ => PluralCategory.Other,
    };

    /// <summary>A catalogue held in memory, for a language the app has none in.</summary>
    /// <param name="entries">Its entries</param>
    private sealed class Catalogue(params (string Name, string Text)[] entries) : ResourceManager
    {
        public override string? GetString(string name, CultureInfo? culture)
            => entries.FirstOrDefault(e => e.Name == name).Text;
    }
}
