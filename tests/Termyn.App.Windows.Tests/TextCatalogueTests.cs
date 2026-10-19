using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
        // DueTimeAccessible — and the longer one's use would otherwise count for the shorter.
        var unused = Entries().Select(e => e.Name).Where(name => !Regex.IsMatch(source, $@"\bStrings\.{name}\b"));

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
}
