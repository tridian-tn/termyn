using System.Text;
using Termyn.Core.Model;

namespace Termyn.Core.Filters;

/// <summary>The project and label names in the account, which a query's terms are read against.</summary>
public sealed class FilterVocabulary
{
    public FilterVocabulary(IEnumerable<string> projects, IEnumerable<string> labels)
    {
        Projects = projects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Labels = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Projects { get; }

    public IReadOnlySet<string> Labels { get; }

    public static FilterVocabulary From(IEnumerable<Project> projects, IEnumerable<Label> labels)
        => new(projects.Select(p => p.Name), labels.Select(l => l.Name));
}

/// <summary>
/// The outcome of parsing a filter. Either the whole query is understood, or none of it is: a
/// partly-applied filter would look like an answer while being the wrong set of tasks.
/// </summary>
/// <param name="Unsupported">The fragment that couldn't be read, for the "open in Todoist" prompt.</param>
public sealed record FilterParse(FilterExpression? Expression, string? Unsupported)
{
    public bool IsSupported => Expression is not null;

    public static FilterParse Ok(FilterExpression expression) => new(expression, null);

    public static FilterParse No(string fragment) => new(null, fragment);
}

/// <summary>
/// Reads the filter grammar Termyn supports: <c>#project</c>, <c>##project</c> (with sub-projects),
/// <c>@label</c>, <c>p1</c>–<c>p4</c>, <c>today</c>, <c>overdue</c>, <c>no date</c>,
/// <c>next N days</c>, <c>search: text</c>, combined with <c>&amp;</c>, <c>|</c>, <c>,</c>,
/// <c>!</c> and parentheses.
/// </summary>
/// <remarks>
/// Precedence is <c>!</c> then <c>&amp;</c> then <c>|</c>/<c>,</c>, left-associative. Adjacent terms
/// with no operator between them are an implicit <c>&amp;</c>, which is how "#Work today" reads.
/// </remarks>
public static class FilterParser
{
    private const string SearchPrefix = "search:";

    /// <summary>
    /// The most tokens a query may carry. Parsing recurses on parentheses and negation, and the
    /// expression the evaluator later walks is recursive too — a long flat run of terms builds a
    /// chain just as deep as nested brackets do. Past some size that overflows the stack, and a
    /// stack overflow can't be caught: the process goes, taking anything unflushed with it. Every
    /// one of those depths is bounded by the number of tokens, so one ceiling covers them all.
    /// </summary>
    private const int MaxTokens = 256;

    /// <summary>Longest window <c>next N days</c> may ask for, in days.</summary>
    /// <remarks>Beyond this the window runs off the end of the calendar and the date maths throws.</remarks>
    private const int MaxDays = 3650;

    public static FilterParse Parse(string? query, FilterVocabulary vocabulary)
    {
        var tokens = Tokenize(query ?? string.Empty);
        if (tokens.Count == 0)
            return FilterParse.No(string.Empty);

        // The raw query rather than the tokens rejoined: this one is already too big, and building
        // a second copy of it to throw away is the work we are refusing it to avoid.
        if (tokens.Count > MaxTokens)
            return FilterParse.No(Shortened(query!));

        var at = 0;
        var expression = ParseOr(tokens, vocabulary, ref at, out var failed);

        if (expression is null)
            return FilterParse.No(Shortened(failed ?? string.Join(' ', tokens)));

        // Trailing tokens mean the query didn't parse as a whole — most likely an unbalanced ')'.
        return at == tokens.Count
            ? FilterParse.Ok(expression)
            : FilterParse.No(Shortened(string.Join(' ', tokens[at..])));
    }

    // ---- Grammar -----------------------------------------------------------------------------------

    private static FilterExpression? ParseOr(List<string> tokens, FilterVocabulary vocabulary, ref int at, out string? failed)
    {
        var left = ParseAnd(tokens, vocabulary, ref at, out failed);
        if (left is null)
            return null;

        while (at < tokens.Count && tokens[at] is "|" or ",")
        {
            at++;
            var right = ParseAnd(tokens, vocabulary, ref at, out failed);
            if (right is null)
                return null;

            left = new FilterExpression.Or(left, right);
        }

        return left;
    }

    private static FilterExpression? ParseAnd(List<string> tokens, FilterVocabulary vocabulary, ref int at, out string? failed)
    {
        var left = ParseUnary(tokens, vocabulary, ref at, out failed);
        if (left is null)
            return null;

        while (at < tokens.Count)
        {
            if (tokens[at] == "&")
                at++;
            else if (!StartsTerm(tokens[at]))
                break;

            var right = ParseUnary(tokens, vocabulary, ref at, out failed);
            if (right is null)
                return null;

            left = new FilterExpression.And(left, right);
        }

        return left;
    }

    private static FilterExpression? ParseUnary(List<string> tokens, FilterVocabulary vocabulary, ref int at, out string? failed)
    {
        failed = null;

        if (at < tokens.Count && tokens[at] == "!")
        {
            at++;
            var operand = ParseUnary(tokens, vocabulary, ref at, out failed);
            return operand is null ? null : new FilterExpression.Not(operand);
        }

        if (at < tokens.Count && tokens[at] == "(")
        {
            at++;
            var inner = ParseOr(tokens, vocabulary, ref at, out failed);
            if (inner is null)
                return null;

            if (at >= tokens.Count || tokens[at] != ")")
            {
                failed = "(";
                return null;
            }

            at++;
            return inner;
        }

        return ParseTerm(tokens, vocabulary, ref at, out failed);
    }

    /// <summary>Reads one term, consuming however many words its name or phrase needs.</summary>
    private static FilterExpression? ParseTerm(List<string> tokens, FilterVocabulary vocabulary, ref int at, out string? failed)
    {
        failed = null;

        // Off the end of the query: an operator at the back is still waiting for a term, and naming
        // it beats reporting nothing at all.
        if (at >= tokens.Count)
        {
            failed = tokens.Count > 0 ? tokens[^1] : string.Empty;
            return null;
        }

        if (IsOperator(tokens[at]))
        {
            failed = tokens[at];
            return null;
        }

        var word = tokens[at];

        if (word.StartsWith("##", StringComparison.Ordinal))
            return ReadName(tokens, vocabulary.Projects, ref at, 2, out failed) is { } sub
                ? new FilterExpression.InProject(sub, IncludeSubProjects: true)
                : null;

        if (word.StartsWith('#'))
            return ReadName(tokens, vocabulary.Projects, ref at, 1, out failed) is { } project
                ? new FilterExpression.InProject(project, IncludeSubProjects: false)
                : null;

        if (word.StartsWith('@'))
            return ReadName(tokens, vocabulary.Labels, ref at, 1, out failed) is { } label
                ? new FilterExpression.HasLabel(label)
                : null;

        if (word.StartsWith(SearchPrefix, StringComparison.OrdinalIgnoreCase))
            return ReadSearch(tokens, ref at, out failed);

        if (TryReadPriority(word, out var priority))
        {
            at++;
            return new FilterExpression.HasPriority(priority);
        }

        if (word.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            at++;
            return new FilterExpression.DueToday();
        }

        if (word.Equals("overdue", StringComparison.OrdinalIgnoreCase))
        {
            at++;
            return new FilterExpression.Overdue();
        }

        if (TryReadNoDate(tokens, ref at))
            return new FilterExpression.NoDate();

        if (TryReadNextDays(tokens, ref at, out var days))
            return new FilterExpression.NextDays(days);

        failed = word;
        return null;
    }

    // ---- Terms -------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a project or label name, which may run to several words. Only the account's own names
    /// can say where one ends: "#My Project" is a single project, while "#Work today" is a project
    /// and a date. The longest run that names something wins; failing that, the first word alone.
    /// </summary>
    private static string? ReadName(List<string> tokens, IReadOnlySet<string> known, ref int at, int prefix, out string? failed)
    {
        var name = tokens[at][prefix..];
        if (name.Length == 0)
        {
            failed = tokens[at];
            return null;
        }

        failed = null;
        var consumed = 1;
        var candidate = new StringBuilder(name);

        for (var i = at + 1; i < tokens.Count && !IsOperator(tokens[i]); i++)
        {
            candidate.Append(' ').Append(tokens[i]);
            if (known.Contains(candidate.ToString()))
            {
                name = candidate.ToString();
                consumed = i - at + 1;
            }
        }

        at += consumed;
        return name;
    }

    /// <summary>Takes the rest of the run as the search text — spaces and all, up to an operator.</summary>
    private static FilterExpression? ReadSearch(List<string> tokens, ref int at, out string? failed)
    {
        var text = new StringBuilder(tokens[at][SearchPrefix.Length..]);
        at++;

        while (at < tokens.Count && !IsOperator(tokens[at]))
        {
            if (text.Length > 0)
                text.Append(' ');
            text.Append(tokens[at]);
            at++;
        }

        if (text.Length == 0)
        {
            failed = SearchPrefix;
            return null;
        }

        failed = null;
        return new FilterExpression.Search(text.ToString());
    }

    private static bool TryReadNoDate(List<string> tokens, ref int at)
    {
        if (!tokens[at].Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;

        // Todoist writes this both ways, and "no due date" reads more naturally.
        if (Word(tokens, at + 1, "date"))
        {
            at += 2;
            return true;
        }

        if (Word(tokens, at + 1, "due") && Word(tokens, at + 2, "date"))
        {
            at += 3;
            return true;
        }

        return false;
    }

    /// <summary>Whether the token at this position is the given keyword, however it is capitalised.</summary>
    private static bool Word(List<string> tokens, int at, string keyword)
        => at < tokens.Count && tokens[at].Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadNextDays(List<string> tokens, ref int at, out int days)
    {
        days = 0;
        if (!tokens[at].Equals("next", StringComparison.OrdinalIgnoreCase))
            return false;

        if (at + 2 >= tokens.Count)
            return false;

        if (!int.TryParse(tokens[at + 1], out days) || days is <= 0 or > MaxDays)
            return false;

        if (!Word(tokens, at + 2, "days") && !Word(tokens, at + 2, "day"))
            return false;

        at += 3;
        return true;
    }

    private static bool TryReadPriority(string word, out Priority priority)
    {
        priority = Priority.P4;
        if (word.Length != 2 || (word[0] != 'p' && word[0] != 'P') || word[1] is < '1' or > '4')
            return false;

        priority = (Priority)(word[1] - '0');
        return true;
    }

    // ---- Lexing ------------------------------------------------------------------------------------

    /// <summary>Cuts a refused fragment down to something a message can carry, on one line.</summary>
    private static string Shortened(string fragment)
    {
        var flat = fragment.ReplaceLineEndings(" ");
        return flat.Length > 80 ? flat[..80] + " …" : flat;
    }

    private static bool IsOperator(string token) => token is "&" or "|" or "," or "(" or ")" or "!";

    private static bool StartsTerm(string token) => token is "(" or "!" || !IsOperator(token);

    /// <summary>
    /// Splits a query into words and operators, stopping once there are already more than the
    /// parser will accept. Reading the rest can only confirm what is known by then, and the query
    /// comes off the account: however long it is, refusing it costs the same.
    /// </summary>
    private static List<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();

        foreach (var c in query)
        {
            if (tokens.Count > MaxTokens)
                break;

            if (char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }

            if (c is '(' or ')' or '&' or '|' or ',')
            {
                Flush();
                tokens.Add(c.ToString());
                continue;
            }

            // Negation only ever leads a term. Mid-word it is ordinary text, so "search: sale!"
            // keeps its exclamation mark.
            if (c == '!' && word.Length == 0)
            {
                tokens.Add("!");
                continue;
            }

            word.Append(c);
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (word.Length == 0)
                return;

            tokens.Add(word.ToString());
            word.Clear();
        }
    }
}
