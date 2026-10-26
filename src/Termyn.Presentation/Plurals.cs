using System.Globalization;
using System.Resources;

namespace Termyn.Presentation;

/// <summary>
/// The forms a counted phrase can take, named the way CLDR names them.
/// </summary>
/// <remarks>
/// A language uses some of these and never all: English has <see cref="One"/> and
/// <see cref="Other"/>, Polish adds <see cref="Few"/> and <see cref="Many"/>, and Arabic uses the
/// lot. <see cref="Other"/> is the one every language has.
/// </remarks>
public enum PluralCategory
{
    Zero,
    One,
    Two,
    Few,
    Many,
    Other,
}

/// <summary>
/// A phrase with a count in it, in the form the catalogue's own language gives that count.
/// </summary>
/// <remarks>
/// A counted phrase is held as a family of entries, one per form, named for the form:
/// <c>StatusTasks_One</c> and <c>StatusTasks_Other</c>. Two was an English assumption. Polish says
/// 2 and 22 one way and 5 and 25 another, and French says 0 the way it says 1, so a translation adds
/// whichever forms its language has, and a form that isn't there falls back to <c>_Other</c>.
///
/// The rule that picks the form is the catalogue's language, not the machine's. A machine set to
/// French with no French catalogue shows the English words, and only English's rule fits them:
/// French's would write "0 task".
/// </remarks>
public static class Plurals
{
    /// <summary>The entry each catalogue names its own language in.</summary>
    public const string LanguageEntry = "CatalogueLanguage";

    private const string OtherSuffix = "_Other";

    /// <summary>
    /// Each catalogue language's rule for a whole number, as CLDR gives it.
    /// </summary>
    /// <remarks>
    /// Only the languages there's a catalogue in. Every count here is a whole number of something,
    /// so the rules' fraction operands don't come into it.
    /// </remarks>
    private static readonly Dictionary<string, Func<int, PluralCategory>> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        // one: i = 1 and v = 0
        ["en"] = n => n == 1 ? PluralCategory.One : PluralCategory.Other,
    };

    /// <summary>
    /// A counted phrase from a catalogue, in the form its language gives the count.
    /// </summary>
    /// <remarks>
    /// Named by its <c>_Other</c> entry so the compiler checks the name: pass it as
    /// <c>nameof(Strings.StatusTasks_Other)</c>, and the other forms are found beside it. A
    /// catalogue in a language with no rule here gets <c>_Other</c> for every count, which reads
    /// wrongly for some counts rather than not at all.
    /// </remarks>
    /// <param name="catalogue">The catalogue the phrase is in</param>
    /// <param name="otherName">The name of the phrase's <c>_Other</c> entry</param>
    /// <param name="count">The count, which takes the phrase's <c>{0}</c></param>
    /// <returns>The phrase, with the count in it</returns>
    public static string Format(ResourceManager catalogue, string otherName, int count)
        => Format(catalogue, otherName, count, RuleFor(catalogue.GetString(LanguageEntry, CultureInfo.CurrentUICulture)));

    /// <summary>A counted phrase, in the form a given rule picks.</summary>
    /// <param name="catalogue">The catalogue the phrase is in</param>
    /// <param name="otherName">The name of the phrase's <c>_Other</c> entry</param>
    /// <param name="count">The count, which takes the phrase's <c>{0}</c></param>
    /// <param name="rule">Which form a count takes</param>
    /// <returns>The phrase, with the count in it</returns>
    internal static string Format(ResourceManager catalogue, string otherName, int count, Func<int, PluralCategory> rule)
    {
        if (!otherName.EndsWith(OtherSuffix, StringComparison.Ordinal))
            throw new ArgumentException($"{otherName} isn't the _Other form of a counted phrase", nameof(otherName));

        var form = otherName[..^OtherSuffix.Length] + "_" + rule(count);
        var text = catalogue.GetString(form, CultureInfo.CurrentUICulture)
            ?? catalogue.GetString(otherName, CultureInfo.CurrentUICulture)
            ?? throw new MissingManifestResourceException($"the catalogue has no {otherName}");

        return string.Format(CultureInfo.CurrentCulture, text, count);
    }

    /// <summary>Whether there's a rule for a language, which every catalogue's has to have.</summary>
    /// <param name="language">A catalogue's language, such as "en"</param>
    /// <returns>Whether its counts are read by its own rule</returns>
    public static bool HasRuleFor(string language) => Rules.ContainsKey(language);

    /// <summary>A language's rule, or one that says <see cref="PluralCategory.Other"/> to everything.</summary>
    private static Func<int, PluralCategory> RuleFor(string? language)
        => language is not null && Rules.TryGetValue(language, out var rule) ? rule : _ => PluralCategory.Other;
}
