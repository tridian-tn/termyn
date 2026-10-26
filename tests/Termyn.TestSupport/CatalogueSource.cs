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
        _folder = Path.Combine(Repository.Root(), "src", project);
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
            .SelectMany(e => PlaceholdersIn(e.Value).Select(placeholder => (e.Name, Placeholder: placeholder, e.Comment)))
            .Where(p => !p.Comment.Contains(p.Placeholder, StringComparison.Ordinal))
            .Select(p => $"{p.Name} {p.Placeholder}");

    /// <summary>
    /// The placeholders in an entry's value, each once and by its number alone.
    /// </summary>
    /// <remarks>
    /// By number because that's how a comment names one: "{0} is the count" explains a {0:N0} as
    /// much as a {0}, and a translator keeps the format with the number.
    /// </remarks>
    /// <param name="value">An entry's value</param>
    /// <returns>Each placeholder, such as "{0}", in the order it first appears</returns>
    public static IEnumerable<string> PlaceholdersIn(string value)
        => FormatItem().Matches(value).Select(m => $"{{{m.Groups[1].Value}}}").Distinct();

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
        var read = Code()
            .SelectMany(file => CatalogueRead().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        return Entries.Select(e => e.Name)
            .Where(name => !readByName.Contains(name))
            .Where(name => !read.Contains(OtherFormOf(name)));
    }

    /// <summary>Whether an entry is one form of a counted phrase, _Other included.</summary>
    /// <param name="name">The entry's name</param>
    /// <returns>Whether it ends in the name of one of CLDR's forms</returns>
    public static bool IsCountedForm(string name) => CountedForm().IsMatch(name);

    /// <summary>The _Other form of the counted phrase an entry's a form of.</summary>
    /// <param name="name">The entry's name</param>
    /// <returns>The _Other form's name, or the entry's own when it isn't a form of anything</returns>
    public static string OtherFormOf(string name) => CountedForm().Replace(name, "_Other");

    /// <summary>
    /// Text in the project's code that reads like something said to a person, rather than coming
    /// out of the catalogue.
    /// </summary>
    /// <remarks>
    /// Told apart by its shape, which can't be done perfectly: two words side by side, or a word
    /// with a capital, the way a label or a sentence is written. A placeholder counts as a word, so
    /// a format string's wording is caught however its values are put in. A single lower-case
    /// word isn't caught, since it's as often a field Todoist reads ("content") as a word a person
    /// does, and nor is a unit of one letter. What's caught is the bulk of it — the sentences and
    /// the labels.
    ///
    /// Left alone: what the log says, which is for whoever reads the log; an attribute's arguments;
    /// anything <see cref="ProtocolText"/> calls protocol, or that's a date pattern or a dotted name;
    /// and an exception only a bug can raise. That message does reach the status bar, since the
    /// window shows whatever an exception says, but only as a bug report would — and in the English
    /// the log has it in too. An exception a person can cause, like an operation that can't be done
    /// now, is checked like anything else.
    ///
    /// This reads the code's syntax and not its types, so a log is known by what it's called: a
    /// receiver named log, whatever its case or leading underscores.
    /// </remarks>
    /// <param name="namingCalls">
    /// Methods whose first argument is a name the code keeps rather than words a person reads, such
    /// as an id that settings remember something by. Only that argument of a call to one of them is
    /// left alone, so the same words written anywhere else are still caught.
    /// </param>
    /// <returns>Each piece of text, with the file and line it's on</returns>
    public IEnumerable<string> InlineProse(params string[] namingCalls)
        => Code().SelectMany(file => Prose(File.ReadAllText(file), Path.GetFileName(file), namingCalls));

    /// <summary>
    /// The text in a piece of code that <see cref="InlineProse"/> would report, so what it catches
    /// and what it lets go can be shown on code written to show it.
    /// </summary>
    /// <param name="code">C#, as it would be written in a file</param>
    /// <param name="namingCalls">Methods whose first argument is a name the code keeps, as for <see cref="InlineProse"/></param>
    /// <returns>Each piece of text, with the line it's on</returns>
    public static IEnumerable<string> InlineProseIn(string code, params string[] namingCalls)
        => Prose(code, "code", namingCalls);

    private static IEnumerable<string> Prose(string code, string file, string[] namingCalls)
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

            if (text is null || !ReadsAsProse(FormatItem().Replace(text, Hole)) || IsExcused(node) || NamesSomething(node, namingCalls))
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

    /// <summary>
    /// What an interpolation's hole, or a format string's placeholder, is read as: a word, but not
    /// a word of English.
    /// </summary>
    private const string Hole = "{}";

    /// <summary>Whether some text is shaped like something said to a person.</summary>
    private static bool ReadsAsProse(string text)
        => !ProtocolText.IsProtocol(text)
        && !DottedName().IsMatch(text)
        && !DatePattern().IsMatch(text)
        && (TwoWords().IsMatch(text) || CapitalisedWord().IsMatch(text));

    /// <summary>
    /// Whether some text is somewhere prose is allowed: in what the log's told, in an exception
    /// only a bug can raise, or in an attribute.
    /// </summary>
    private static bool IsExcused(SyntaxNode node)
        => node.Ancestors().Any(a => a switch
        {
            AttributeSyntax => true,
            ConditionalAccessExpressionSyntax { Expression: var receiver } when IsLog(receiver) => true,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var receiver } } when IsLog(receiver) => true,
            ObjectCreationExpressionSyntax { Type: var type } => RaisedOnlyByBugs.Contains(type.ToString()),
            _ => false,
        });

    /// <summary>Whether something's called log: <c>_log</c>, <c>log</c>, <c>this._log</c>, <c>_shell.Log</c>.</summary>
    private static bool IsLog(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText.TrimStart('_').Equals("log", StringComparison.OrdinalIgnoreCase),
        MemberAccessExpressionSyntax access => IsLog(access.Name),
        _ => false,
    };

    /// <summary>Whether some text is the first argument of a call to one of the methods given.</summary>
    private static bool NamesSomething(SyntaxNode node, string[] namingCalls)
        => node.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } arguments } argument
        && arguments.Arguments[0] == argument
        && namingCalls.Contains(call.Expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        });

    /// <summary>
    /// The exceptions only a bug can raise: an argument the code was wrongly handed, a branch it
    /// shouldn't have reached, a catalogue missing from the build. Not one a person's own doing can
    /// cause, such as InvalidOperationException for something that can't be done now.
    /// </summary>
    private static readonly HashSet<string> RaisedOnlyByBugs =
    [
        "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException", "UnreachableException",
        "MissingManifestResourceException",
    ];

    /// <summary>A format item, with its number captured: {0}, {1,5}, {0:N0}.</summary>
    [GeneratedRegex(@"\{(\d+)(,[^}:]*)?(:[^}]*)?\}")]
    private static partial Regex FormatItem();

    /// <summary>An entry read from the catalogue's generated class, with its name captured whole.</summary>
    [GeneratedRegex(@"\bStrings\.(\w+)")]
    private static partial Regex CatalogueRead();

    /// <summary>The suffix of a counted phrase's forms, as CLDR names them for whole numbers.</summary>
    [GeneratedRegex("_(Zero|One|Two|Few|Many|Other)$")]
    private static partial Regex CountedForm();

    /// <summary>
    /// Two words side by side, at least one of them a real one: "when due", "{0} at {1}", "{0}h
    /// before". Two holes with a space between are layout, not wording. Any space between counts,
    /// a non-breaking one or a line break included.
    /// </summary>
    [GeneratedRegex(@"\p{L}{2,}\s+(\p{L}{2,}|\{\})|\{\}\p{L}*\s+\p{L}{2,}")]
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
