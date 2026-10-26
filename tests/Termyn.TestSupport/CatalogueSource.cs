using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Termyn.TestSupport;

/// <summary>One entry of a catalogue, as its .resx is written.</summary>
/// <param name="Name">The entry's key</param>
/// <param name="Value">What it says</param>
/// <param name="Comment">What it tells a translator, or empty when it tells them nothing</param>
public sealed record CatalogueEntry(string Name, string Value, string Comment);

/// <summary>
/// A project's catalogue as it's written in the source, comments and all, beside the code that's
/// meant to read it.
/// </summary>
/// <remarks>
/// Read from the source tree rather than the build. A comment isn't built into the app — it's only
/// ever for whoever translates the entry, who otherwise gets "Clear" with no idea what it clears —
/// and whether an entry's read, or text has gone round the catalogue altogether, is a question about
/// the code as it's written.
/// </remarks>
public sealed partial class CatalogueSource
{
    private readonly string _folder;

    /// <summary>The catalogue of a project under <c>src</c>, and that project's code.</summary>
    /// <param name="project">The project's folder name, such as "Termyn.Presentation"</param>
    public CatalogueSource(string project)
    {
        _folder = Path.Combine(RepoRoot(), "src", project);
        Entries = XDocument.Load(Path.Combine(_folder, "Strings.resx"))
            .Root!.Elements("data")
            .Select(d => new CatalogueEntry(
                (string)d.Attribute("name")!,
                (string?)d.Element("value") ?? string.Empty,
                (string?)d.Element("comment") ?? string.Empty))
            .ToList();
    }

    /// <summary>The catalogue's entries, in the order they're written.</summary>
    public IReadOnlyList<CatalogueEntry> Entries { get; }

    /// <summary>The entries that don't tell a translator anything about where they appear.</summary>
    /// <returns>Each one's name</returns>
    public IEnumerable<string> Bare()
        => Entries.Where(e => string.IsNullOrWhiteSpace(e.Comment)).Select(e => e.Name);

    /// <summary>
    /// The placeholders an entry's comment doesn't mention.
    /// </summary>
    /// <remarks>
    /// A translation that drops a {0} still builds and still loads, then shows a sentence with a
    /// hole in it. Saying what each one stands for is what keeps it in.
    /// </remarks>
    /// <returns>Each one, as the entry's name and the placeholder</returns>
    public IEnumerable<string> Unexplained()
        => Entries
            .SelectMany(e => Placeholder().Matches(e.Value).Select(m => (e.Name, Placeholder: m.Value, e.Comment)))
            .Where(p => !p.Comment.Contains(p.Placeholder, StringComparison.Ordinal))
            .Select(p => $"{p.Name} {p.Placeholder}");

    /// <summary>
    /// The entries nothing in the project reads.
    /// </summary>
    /// <remarks>
    /// An entry nothing reads still gets translated, and goes on being translated long after the
    /// window that said it has gone.
    ///
    /// Matched as a whole name: several keys are the start of another — DueTime and
    /// DueTimeAccessible — and the longer one's use would otherwise count for the shorter. A counted
    /// phrase's forms are read through its _Other one, which is the name the code hands over, and
    /// some entries are read by a name held elsewhere, such as the catalogue's language.
    /// </remarks>
    /// <param name="readByName">The entries read by a name held elsewhere, which aren't looked for</param>
    /// <returns>Each one's name</returns>
    public IEnumerable<string> Unused(params string[] readByName)
    {
        var source = string.Concat(Code().Select(File.ReadAllText));

        return Entries.Select(e => e.Name)
            .Where(name => !readByName.Contains(name))
            .Where(name => !Regex.IsMatch(source, $@"\bStrings\.{CountedForm().Replace(name, "_Other")}\b"));
    }

    /// <summary>
    /// Text in the project's code that reads like something said to a person, rather than coming
    /// out of the catalogue.
    /// </summary>
    /// <remarks>
    /// Told apart by its shape, which can't be done perfectly: two words side by side, or a word
    /// with a capital, the way a label or a sentence is written. A single lower-case word isn't
    /// caught, since it's as often a field Todoist reads ("content") as a word a person does, and
    /// nor is a unit of one letter. What's caught is the bulk of it — the sentences and the labels.
    ///
    /// Left alone: what the log says, which is for whoever reads the log; an exception that means
    /// the code's been called wrongly, which no user is shown; an attribute's arguments; and
    /// anything <see cref="ProtocolText"/> calls protocol, or that's a date pattern or a dotted
    /// name. The log is known by the name it's held under, <c>_log</c>, since this reads the
    /// code's syntax and not its types.
    /// </remarks>
    /// <param name="allowed">Text that looks like prose and isn't, each one for a reason the caller gives</param>
    /// <returns>Each piece of text, with the file and line it's on</returns>
    public IEnumerable<string> InlineProse(params string[] allowed)
        => Code().SelectMany(file => Prose(File.ReadAllText(file), Path.GetFileName(file), allowed));

    /// <summary>
    /// The text in a piece of code that <see cref="InlineProse"/> would report, so what it catches
    /// and what it lets go can be shown on code written to show it.
    /// </summary>
    /// <param name="code">C#, as it would be written in a file</param>
    /// <returns>Each piece of text, with the line it's on</returns>
    public static IEnumerable<string> InlineProseIn(string code) => Prose(code, "code", []);

    private static IEnumerable<string> Prose(string code, string file, string[] allowed)
    {
        foreach (var node in CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes())
        {
            var text = node switch
            {
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
                InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(
                    part => part is InterpolatedStringTextSyntax words ? words.TextToken.ValueText : Hole)),
                _ => null,
            };

            if (text is null || allowed.Contains(text) || !ReadsAsProse(text) || IsExcused(node))
                continue;

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            yield return $"{file}:{line}: {text}";
        }
    }

    /// <summary>The project's own code, without what the build generates into obj and bin.</summary>
    private IEnumerable<string> Code()
        => Directory.EnumerateFiles(_folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(_folder, f).Split(Path.DirectorySeparatorChar).Any(d => d is "obj" or "bin"))
            .Order(StringComparer.Ordinal);

    /// <summary>What an interpolation's hole is read as: a word, but not a word of English.</summary>
    private const string Hole = "{}";

    /// <summary>Whether some text is shaped like something said to a person.</summary>
    private static bool ReadsAsProse(string text)
        => !ProtocolText.IsProtocol(text)
        && !DottedName().IsMatch(text)
        && !DatePattern().IsMatch(text)
        && (TwoWords().IsMatch(text) || CapitalisedWord().IsMatch(text));

    /// <summary>
    /// Whether some text is somewhere prose is allowed: in what the log's told, in an exception
    /// that means the code's been called wrongly, or in an attribute.
    /// </summary>
    private static bool IsExcused(SyntaxNode node)
        => node.Ancestors().Any(a => a switch
        {
            AttributeSyntax => true,
            ConditionalAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "_log" } } => true,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "_log" } } } => true,
            ObjectCreationExpressionSyntax { Type: var type } => ProgrammingErrors.Contains(type.ToString()),
            _ => false,
        });

    /// <summary>
    /// The exceptions that mean the code's been called wrongly, whose message is for whoever wrote
    /// the call. Not every exception: some carry what the user's told.
    /// </summary>
    private static readonly HashSet<string> ProgrammingErrors =
    [
        "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException", "InvalidOperationException",
        "NotSupportedException", "NotImplementedException", "UnreachableException", "MissingManifestResourceException",
    ];

    /// <summary>The folder with <c>Termyn.slnx</c> in it, found from this file or from the build.</summary>
    /// <param name="source">This file's path, filled in by the compiler</param>
    /// <returns>The repository's root</returns>
    public static string RepoRoot([CallerFilePath] string source = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(source), AppContext.BaseDirectory })
        {
            for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Termyn.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("couldn't find Termyn.slnx above the tests or their sources");
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    /// <summary>The suffix of a counted phrase's forms, other than the _Other one every phrase has.</summary>
    [GeneratedRegex("_(Zero|One|Two|Few|Many)$")]
    private static partial Regex CountedForm();

    /// <summary>
    /// Two words side by side, at least one of them a real one: "when due", "{0} at {1}", "{0}h
    /// before". Two holes with a space between are layout, not wording.
    /// </summary>
    [GeneratedRegex(@"\p{L}{2,}[ \t]+(\p{L}{2,}|\{\})|\{\}\p{L}*[ \t]+\p{L}{2,}")]
    private static partial Regex TwoWords();

    /// <summary>
    /// A word with a capital and then lower case, the way a label's written: "Today", "Del". Not
    /// part of a name with an underscore or a digit in it, like "_Other".
    /// </summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])\p{Lu}\p{Ll}+(?![\p{L}\p{N}_])")]
    private static partial Regex CapitalisedWord();

    /// <summary>A name with dots in it, like an assembly's or a namespace's.</summary>
    [GeneratedRegex(@"^\w+(\.\w+)+$")]
    private static partial Regex DottedName();

    /// <summary>A date or time pattern, made only of the letters .NET reads in one: "d MMM yyyy".</summary>
    [GeneratedRegex(@"^[dfFghHKmMstyz\s,.:/'-]+$")]
    private static partial Regex DatePattern();
}
