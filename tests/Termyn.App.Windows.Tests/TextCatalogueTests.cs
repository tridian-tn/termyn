using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
public partial class TextCatalogueTests
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
        var entries = Entries().ToList();
        var forms = entries.Where(e => CountedForm().IsMatch(e.Name) || e.Name.EndsWith("_Other", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(forms);
        Assert.Empty(forms.Where(e => !entries.Any(other => other.Name == CountedForm().Replace(e.Name, "_Other"))).Select(e => e.Name));
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
    {
        // Read from the source file, since a comment isn't built into the app: it's only ever for
        // whoever translates the entry, who otherwise gets "Clear" with no idea what it clears.
        var bare = Entries().Where(e => string.IsNullOrWhiteSpace(e.Comment)).Select(e => e.Name);

        Assert.Empty(bare);
    }

    [WinFormsFact]
    public void Every_placeholder_is_explained()
    {
        // A translation that drops a {0} still builds and still loads, then shows a sentence with a
        // hole in it. Saying what each one stands for is what keeps it in.
        var unexplained = Entries()
            .SelectMany(e => Regex.Matches(e.Value, @"\{\d+\}").Select(m => (e.Name, Placeholder: m.Value, e.Comment)))
            .Where(p => !p.Comment.Contains(p.Placeholder, StringComparison.Ordinal))
            .Select(p => $"{p.Name} {p.Placeholder}");

        Assert.Empty(unexplained);
    }

    [WinFormsFact]
    public void Every_entry_is_used()
    {
        // An entry nothing reads still gets translated, and goes on being translated long after
        // the window that said it has gone.
        var source = string.Concat(
            Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Termyn.App.Windows"), "*.cs")
                .Select(File.ReadAllText));

        // Matched as a whole name: several keys are the start of another — DueTime and
        // DueTimeAccessible — and the longer one's use would otherwise count for the shorter. Two
        // kinds are read by name instead: the catalogue's language, which Plurals reads, and a
        // counted phrase's forms, which it finds beside the _Other one it's handed.
        var unused = Entries().Select(e => e.Name)
            .Where(name => name != Plurals.LanguageEntry)
            .Where(name => !Regex.IsMatch(source, $@"\bStrings\.{CountedForm().Replace(name, "_Other")}\b"));

        Assert.Empty(unused);
    }

    /// <summary>The catalogue's entries as written in the source, with their comments.</summary>
    private static IEnumerable<(string Name, string Value, string Comment)> Entries()
        => XDocument.Load(Path.Combine(RepoRoot(), "src", "Termyn.App.Windows", "Strings.resx"))
            .Root!.Elements("data")
            .Select(d => (
                (string)d.Attribute("name")!,
                (string?)d.Element("value") ?? string.Empty,
                (string?)d.Element("comment") ?? string.Empty));

    /// <summary>The folder with <c>Termyn.slnx</c> in it, found from this file or from the build.</summary>
    /// <param name="source">This file's path, filled in by the compiler</param>
    /// <returns>The repository's root</returns>
    private static string RepoRoot([CallerFilePath] string source = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(source), AppContext.BaseDirectory })
        {
            for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Termyn.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("couldn't find Termyn.slnx above the tests or their sources");
    }

    /// <summary>The suffix of a counted phrase's forms, other than the _Other one every phrase has.</summary>
    [GeneratedRegex("_(Zero|One|Two|Few|Many)$")]
    private static partial Regex CountedForm();
}
