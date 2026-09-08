using System.Globalization;
using System.Text;
using Termyn.Core.Model;

namespace Termyn.Core.Filters;

/// <summary>The names in the account a query's terms are read against.</summary>
/// <remarks>
/// Names are what tell the parser where one term ends, so anything a term can name has to be here:
/// without the section names, <c>/Next Up today</c> has no way of knowing the section isn't called
/// "Next Up today".
/// </remarks>
public sealed class FilterVocabulary
{
    public FilterVocabulary(IEnumerable<string> projects, IEnumerable<string> labels, IEnumerable<string>? sections = null)
    {
        Projects = projects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Labels = labels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Sections = (sections ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Projects { get; }

    public IReadOnlySet<string> Labels { get; }

    public IReadOnlySet<string> Sections { get; }

    public static FilterVocabulary From(
        IEnumerable<Project> projects,
        IEnumerable<Label> labels,
        IEnumerable<Section>? sections = null)
        => new(projects.Select(p => p.Name), labels.Select(l => l.Name), sections?.Select(s => s.Name));
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
/// Reads the filter grammar Termyn supports, which is everything in Todoist's that a task can
/// answer on its own.
/// </summary>
/// <remarks>
/// Where it goes: <c>#project</c> and <c>##project</c> (with sub-projects), <c>/section</c>,
/// <c>%label</c> and <c>@label</c>, <c>no labels</c>. What it is: <c>p1</c>–<c>p4</c> (or
/// <c>priority 1</c>–<c>priority 4</c>, or <c>no priority</c>), <c>recurring</c>, <c>subtask</c>,
/// <c>view all</c>. When it is: <c>today</c>, <c>tomorrow</c>, <c>yesterday</c>, <c>overdue</c>
/// (<c>over due</c>, <c>od</c>), <c>no date</c>, <c>no time</c>, <c>next N days</c>, <c>N days</c>,
/// <c>-N days</c>, and <c>due:</c>, <c>date:</c>, <c>deadline:</c>, <c>created:</c> with their
/// <c>before:</c> and <c>after:</c> forms. Plus <c>no deadline</c> and <c>search: text</c>.
///
/// Who it's for: <c>assigned</c>, <c>assigned to: me</c> (or <c>:to_me:</c>), <c>assigned to:
/// others</c> (or <c>:to_others:</c>), <c>assigned by: me</c>, <c>added by: me</c>, and
/// <c>shared</c>.
///
/// Days are written as <c>today</c>, a weekday, <c>yyyy-MM-dd</c>, or a count like <c>-30 days</c>.
/// Two more are named by something other than the query: <c>next week</c> — the day the account
/// puts things off until, with <c>N weeks after next week</c> closing a week-long window — and
/// <c>first day</c>, the start of the coming month.
///
/// Terms combine with <c>&amp;</c>, <c>|</c>, <c>,</c>, <c>!</c> and parentheses. Precedence is
/// <c>!</c> then <c>&amp;</c> then <c>|</c>/<c>,</c>, left-associative. Adjacent terms with no
/// operator between them are an implicit <c>&amp;</c>, which is how "#Work today" reads.
///
/// Left out, and refused by name rather than guessed at: naming a person rather than yourself,
/// which needs the account's collaborators; which workspace a task is in; the wildcard forms
/// (<c>%email*</c>, <c>#\*name</c>, and the <c>!/*</c> that means "in no section"); and
/// <c>uncompletable</c>, which no field here is known to answer.
/// </remarks>
public static class FilterParser
{
    private const string SearchPrefix = "search:";

    /// <summary>The days of the week, indexed the way <see cref="DayOfWeek"/> counts them.</summary>
    private static readonly string[] Weekdays =
        ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

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

    /// <summary>How far past "next week" the <c>N weeks after</c> form may reach, for the same reason.</summary>
    private const int MaxWeeks = MaxDays / 7;

    public static FilterParse Parse(string? query, FilterVocabulary vocabulary)
    {
        var text = query ?? string.Empty;
        var tokens = Tokenize(text, out var unclosed);

        // A quote or an escape left open means the query stops part-way through a name. Reading the
        // rest as though it were finished would turn a broken query into a different working one —
        // "#Foo\" into "#Foo" — and answering a question nobody asked is the whole of what
        // all-or-nothing exists to prevent.
        if (unclosed)
            return FilterParse.No(Shortened(text));

        if (tokens.Count == 0)
            return FilterParse.No(string.Empty);

        // The raw query rather than the tokens rejoined: this one is already too big, and building
        // a second copy of it to throw away is the work we are refusing it to avoid.
        if (tokens.Count > MaxTokens)
            return FilterParse.No(Shortened(text));

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

        // Todoist writes a label "%name" now; "@name" is the older form and still works there, so
        // both are read here rather than the one that is on its way out.
        if (word.StartsWith('@') || word.StartsWith('%'))
            return ReadName(tokens, vocabulary.Labels, ref at, 1, out failed) is { } label
                ? new FilterExpression.HasLabel(label)
                : null;

        if (word.StartsWith('/'))
            return ReadName(tokens, vocabulary.Sections, ref at, 1, out failed) is { } section
                ? new FilterExpression.InSection(section)
                : null;

        if (word.StartsWith(SearchPrefix, StringComparison.OrdinalIgnoreCase))
            return ReadSearch(tokens, ref at, out failed);

        if (TryReadPriority(tokens, ref at, out var priority))
            return new FilterExpression.HasPriority(priority);

        if (TryReadEverything(tokens, ref at))
            return new FilterExpression.Everything();

        // The dated families all read the same way, differing only in what they ask the day about.
        if (Names(word, "created"))
            return ReadDated(tokens, ref at, out failed, (bound, day) => new FilterExpression.Created(bound, day));

        // "date:" is what Todoist called this before "due:", and both still work there.
        if (Names(word, "due") || Names(word, "date"))
            return ReadDated(tokens, ref at, out failed, (bound, day) => new FilterExpression.Due(bound, day));

        if (Names(word, "deadline"))
            return ReadDated(tokens, ref at, out failed, (bound, day) => new FilterExpression.Deadline(bound, day));

        if (TryReadWho(tokens, ref at, out failed) is { } who)
            return who;

        if (failed is not null)
            return null;

        if (TryReadNothing(tokens, ref at) is { } nothing)
            return nothing;

        if (TryReadPlainTerm(tokens, ref at) is { } plain)
            return plain;

        if (TryReadNextDays(tokens, ref at, out var days))
            return new FilterExpression.NextDays(days);

        if (TryReadWindow(tokens, ref at) is { } window)
            return window;

        // A day on its own is Todoist's shorthand for due on it. A bare count of days isn't one of
        // those: "3 days" is the window just read above, so a count that got this far — "0 days" —
        // is a query nobody meant rather than a day to fall back on.
        if (TryReadDay(tokens, ref at, counts: false) is { } day)
            return new FilterExpression.Due(DayBound.On, day);

        failed = word;
        return null;
    }

    /// <summary>
    /// Reads the terms about people: <c>assigned to:</c>, <c>assigned by:</c>, <c>added by:</c>.
    /// </summary>
    /// <remarks>
    /// Only <c>me</c> and <c>others</c>. Naming anyone else needs the account's collaborators, which
    /// this client doesn't sync — so a person's name is refused with the term named rather than
    /// answered with a guess about which Sam.
    ///
    /// Which pairs exist follows Todoist rather than symmetry: it has no "assigned by: others" and
    /// no "added by: others", so neither is read here. Inventing them would mean a query that works
    /// in Termyn and fails in Todoist, which is the wrong way round for a client to differ.
    /// </remarks>
    /// <param name="tokens">The query</param>
    /// <param name="at">Where to read from, advanced past what was read</param>
    /// <param name="failed">The fragment that couldn't be read, when it couldn't</param>
    /// <returns>The term, or null when this isn't one</returns>
    private static FilterExpression? TryReadWho(List<string> tokens, ref int at, out string? failed)
    {
        failed = null;

        var assigned = Word(tokens, at, "assigned");
        if (!assigned && !Word(tokens, at, "added"))
            return null;

        // "assigned" on its own is a term of its own, read elsewhere; this one wants the pair.
        var to = Word(tokens, at + 1, "to:");
        var by = Word(tokens, at + 1, "by:");
        if (!to && !by)
            return null;

        var who = at + 2 < tokens.Count ? tokens[at + 2].ToLowerInvariant() : string.Empty;

        FilterExpression? term = (assigned, to, who) switch
        {
            (true, true, "me") => new FilterExpression.AssignedToMe(),
            (true, true, "others") => new FilterExpression.AssignedToOthers(),
            (true, false, "me") => new FilterExpression.AssignedByMe(),
            (false, false, "me") => new FilterExpression.AddedByMe(),
            _ => null,
        };

        if (term is null)
        {
            // Named with the person in it, so the notice says which part it couldn't answer.
            failed = string.Join(' ', tokens[at..Math.Min(tokens.Count, at + 3)]);
            return null;
        }

        at += 3;
        return term;
    }

    /// <summary>
    /// Terms that are a word or two and nothing more.
    /// </summary>
    /// <remarks>
    /// <c>overdue</c> is written three ways in Todoist and all three mean it. The rest are single
    /// words, and each stands for a question the task can answer about itself.
    /// </remarks>
    /// <param name="tokens">The query</param>
    /// <param name="at">Where to read from, advanced past what was read</param>
    /// <returns>The term, or null when this isn't one</returns>
    private static FilterExpression? TryReadPlainTerm(List<string> tokens, ref int at)
    {
        if (Word(tokens, at, "over") && Word(tokens, at + 1, "due"))
        {
            at += 2;
            return new FilterExpression.Overdue();
        }

        FilterExpression? term = tokens[at].ToLowerInvariant() switch
        {
            "today" => new FilterExpression.DueToday(),
            "tomorrow" => new FilterExpression.Due(DayBound.On, FilterDay.FromToday(1)),
            "yesterday" => new FilterExpression.Due(DayBound.On, FilterDay.FromToday(-1)),
            "overdue" or "od" => new FilterExpression.Overdue(),
            "recurring" => new FilterExpression.Recurring(),
            "subtask" => new FilterExpression.Subtask(),
            "assigned" => new FilterExpression.Assigned(),
            "shared" => new FilterExpression.Shared(),

            // Not in the help pages, but it's what Todoist writes into the saved filters it gives
            // every account — so an account arrives holding two filters spelled this way, and
            // refusing them would leave the app unable to read what it was handed on day one.
            ":to_me:" => new FilterExpression.AssignedToMe(),
            ":to_others:" => new FilterExpression.AssignedToOthers(),
            _ => null,
        };

        if (term is not null)
            at++;

        return term;
    }

    /// <summary>
    /// Reads the <c>no …</c> family, each of which asks for the tasks missing something.
    /// </summary>
    /// <remarks>
    /// <c>no date</c> is written both ways and "no due date" reads more naturally. <c>no priority</c>
    /// is p4 rather than a state of its own: Todoist gives every task a priority and calls the
    /// lowest one none.
    /// </remarks>
    /// <param name="tokens">The query</param>
    /// <param name="at">Where to read from, advanced past what was read</param>
    /// <returns>The term, or null when this isn't one</returns>
    private static FilterExpression? TryReadNothing(List<string> tokens, ref int at)
    {
        if (!Word(tokens, at, "no"))
            return null;

        if (Word(tokens, at + 1, "due") && Word(tokens, at + 2, "date"))
        {
            at += 3;
            return new FilterExpression.NoDate();
        }

        FilterExpression? term = tokens.Count > at + 1 ? tokens[at + 1].ToLowerInvariant() switch
        {
            "date" => new FilterExpression.NoDate(),
            "time" => new FilterExpression.NoTime(),
            "deadline" => new FilterExpression.NoDeadline(),
            "label" or "labels" => new FilterExpression.NoLabels(),
            "priority" => new FilterExpression.HasPriority(Priority.P4),
            _ => null,
        } : null;

        if (term is not null)
            at += 2;

        return term;
    }

    /// <summary>
    /// Reads a bare window of days: <c>3 days</c> ahead, or <c>-3 days</c> behind.
    /// </summary>
    /// <remarks>
    /// The same window <c>next N days</c> asks for, written the shorter way, and its mirror into the
    /// past. Both count today, so <c>3 days</c> and <c>-3 days</c> overlap on it — which is what
    /// makes each of them three days rather than four.
    /// </remarks>
    /// <param name="tokens">The query</param>
    /// <param name="at">Where to read from, advanced past what was read</param>
    /// <returns>The term, or null when this isn't one</returns>
    private static FilterExpression? TryReadWindow(List<string> tokens, ref int at)
    {
        if (!int.TryParse(tokens[at], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var days))
            return null;

        if (days == 0 || Math.Abs(days) > MaxDays)
            return null;

        if (!Word(tokens, at + 1, "days") && !Word(tokens, at + 1, "day"))
            return null;

        at += 2;
        return days > 0 ? new FilterExpression.NextDays(days) : new FilterExpression.LastDays(-days);
    }

    /// <summary>Whether a word names a term, with or without the colon Todoist writes after it.</summary>
    private static bool Names(string word, string name)
        => word.Equals(name, StringComparison.OrdinalIgnoreCase)
        || (word.Length == name.Length + 1
            && word[^1] == ':'
            && word.AsSpan(0, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Reads a term that names a day — <c>created:</c>, <c>due:</c>, <c>date:</c>, <c>deadline:</c>.
    /// </summary>
    /// <remarks>
    /// All four are written the same way, with the bound as its own word: <c>created: today</c>,
    /// <c>due before: sat</c>, <c>deadline after: 2026-01-01</c>. Nothing here guesses at a term it
    /// only half reads; one with no readable day after it is refused with that day named, so the
    /// notice says which part was the trouble.
    /// </remarks>
    /// <param name="tokens">The query</param>
    /// <param name="at">Where to read from, advanced past what was read</param>
    /// <param name="failed">The fragment that couldn't be read, when it couldn't</param>
    /// <param name="make">Builds the term this one is, once the bound and the day are known</param>
    /// <returns>The term, or null when the day couldn't be read</returns>
    private static FilterExpression? ReadDated(
        List<string> tokens,
        ref int at,
        out string? failed,
        Func<DayBound, FilterDay, FilterExpression> make)
    {
        failed = null;
        var start = at;
        var bound = DayBound.On;
        at++;

        // "created:" carries its colon and the day follows; "created" spells the bound out first.
        if (!tokens[start].EndsWith(':'))
        {
            if (at >= tokens.Count)
            {
                failed = tokens[start];
                return null;
            }

            bound = tokens[at].ToLowerInvariant() switch
            {
                "before:" => DayBound.Before,
                "after:" => DayBound.After,
                _ => (DayBound)(-1),
            };

            if (bound is (DayBound)(-1))
            {
                failed = $"{tokens[start]} {tokens[at]}";
                return null;
            }

            at++;
        }

        if (TryReadDay(tokens, ref at) is not { } day)
        {
            at = start;
            failed = string.Join(' ', tokens[start..Math.Min(tokens.Count, start + 3)]);
            return null;
        }

        return make(bound, day);
    }

    /// <summary>
    /// Reads the day a dated term names: <c>today</c>, <c>tomorrow</c>, <c>yesterday</c>, a weekday,
    /// a date, or a count of days from today.
    /// </summary>
    /// <remarks>
    /// The count keeps its sign — Todoist writes a window into the past as <c>-30 days</c> — and a
    /// bare <c>30 days</c> is read as ahead, which is how the same number reads in <c>next 30
    /// days</c>. Todoist also writes that one <c>in 30 days</c>, so a leading "in" is stepped over.
    ///
    /// A weekday stays a weekday rather than becoming an offset here: which day "sat" lands on
    /// depends on what today is, and that isn't known until the filter runs.
    /// </remarks>
    /// <param name="tokens">The query</param>
    /// <param name="at">Where to read from, advanced past what was read</param>
    /// <param name="counts">Whether a count of days names a day here, or is somebody else's term</param>
    /// <returns>The day, or null when nothing there names one</returns>
    private static FilterDay? TryReadDay(List<string> tokens, ref int at, bool counts = true)
    {
        // "in 7 days" and "7 days" are the same window, and the word carries nothing else.
        var from = Word(tokens, at, "in") ? at + 1 : at;

        if (from >= tokens.Count)
            return null;

        switch (tokens[from].ToLowerInvariant())
        {
            case "today":
                at = from + 1;
                return FilterDay.Today;

            case "tomorrow":
                at = from + 1;
                return FilterDay.FromToday(1);

            case "yesterday":
                at = from + 1;
                return FilterDay.FromToday(-1);
        }

        if (TryReadWeekday(tokens[from]) is { } weekday)
        {
            at = from + 1;
            return FilterDay.OnWeekday(weekday);
        }

        if (TryReadAnchoredDay(tokens, from, ref at) is { } anchored)
            return anchored;

        if (DateOnly.TryParseExact(tokens[from], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            at = from + 1;
            return FilterDay.On(date);
        }

        if (counts
            && int.TryParse(tokens[from], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var days)
            && Math.Abs(days) <= MaxDays
            && (Word(tokens, from + 1, "days") || Word(tokens, from + 1, "day")))
        {
            at = from + 2;
            return FilterDay.FromToday(days);
        }

        return null;
    }

    /// <summary>
    /// Reads the days something other than the query names: <c>next week</c>, <c>first day</c>, and
    /// the <c>N weeks after next week</c> that closes a week-long window.
    /// </summary>
    /// <remarks>
    /// "next week" is a setting, not a date — the day Todoist puts a task off until, which each
    /// account picks for itself. "first day" is the first of the coming month, which is what makes
    /// "before first day" the current calendar month.
    ///
    /// The count in "1 week after next week" is read as a count rather than taken literally, since
    /// it's written as one. Nothing else takes "N weeks after": Todoist has no "2 weeks after
    /// today", and offering one would mean a query that works here and fails there.
    /// </remarks>
    /// <param name="tokens">The query</param>
    /// <param name="from">Where the day starts, past any leading "in"</param>
    /// <param name="at">Advanced past what was read, when something was</param>
    /// <returns>The day, or null when nothing there names one</returns>
    private static FilterDay? TryReadAnchoredDay(List<string> tokens, int from, ref int at)
    {
        if (Word(tokens, from, "next") && Word(tokens, from + 1, "week"))
        {
            at = from + 2;
            return FilterDay.NextWeek();
        }

        if (Word(tokens, from, "first") && Word(tokens, from + 1, "day"))
        {
            at = from + 2;
            return FilterDay.FirstOfMonth;
        }

        if (int.TryParse(tokens[from], NumberStyles.None, CultureInfo.InvariantCulture, out var weeks)
            && weeks is > 0 and <= MaxWeeks
            && (Word(tokens, from + 1, "weeks") || Word(tokens, from + 1, "week"))
            && Word(tokens, from + 2, "after")
            && Word(tokens, from + 3, "next")
            && Word(tokens, from + 4, "week"))
        {
            at = from + 5;
            return FilterDay.NextWeek(weeks);
        }

        return null;
    }

    /// <summary>
    /// Reads a day of the week, spelled out or cut to its first three letters.
    /// </summary>
    /// <remarks>
    /// Todoist writes the short form — <c>due before: sat</c> — and takes the long one too. Three
    /// letters tell the seven days apart on their own, so nothing shorter is read: "s" is two of
    /// them and "t" is another two.
    /// </remarks>
    /// <param name="word">The word to read</param>
    /// <returns>The day, or null when the word doesn't name one</returns>
    private static DayOfWeek? TryReadWeekday(string word)
    {
        var lower = word.ToLowerInvariant();

        for (var day = 0; day < Weekdays.Length; day++)
            if (lower == Weekdays[day] || (lower.Length == 3 && Weekdays[day].StartsWith(lower, StringComparison.Ordinal)))
                return (DayOfWeek)day;

        return null;
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

    /// <summary>
    /// Reads a priority, written either way round.
    /// </summary>
    /// <remarks>
    /// Todoist itself writes the long form: the four filters it puts in every new account are
    /// <c>priority 1</c> through <c>priority 4</c>, so reading only <c>p1</c> refused a query the
    /// user never wrote and can't easily change.
    /// </remarks>
    private static bool TryReadPriority(List<string> tokens, ref int at, out Priority priority)
    {
        priority = Priority.P4;
        var word = tokens[at];

        if (word.Length == 2 && (word[0] == 'p' || word[0] == 'P') && word[1] is >= '1' and <= '4')
        {
            priority = (Priority)(word[1] - '0');
            at++;
            return true;
        }

        if (!word.Equals("priority", StringComparison.OrdinalIgnoreCase)
            || at + 1 >= tokens.Count
            || tokens[at + 1].Length != 1
            || tokens[at + 1][0] is < '1' or > '4')
        {
            return false;
        }

        priority = (Priority)(tokens[at + 1][0] - '0');
        at += 2;
        return true;
    }

    /// <summary>
    /// Reads <c>view all</c>, Todoist's way of asking for everything.
    /// </summary>
    /// <remarks>
    /// Another one every account is given as a saved filter. It matches every task rather than
    /// standing for no filter at all, so it composes: <c>view all &amp; p1</c> reads the way it looks.
    /// </remarks>
    private static bool TryReadEverything(List<string> tokens, ref int at)
    {
        if (!tokens[at].Equals("view", StringComparison.OrdinalIgnoreCase) || !Word(tokens, at + 1, "all"))
            return false;

        at += 2;
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
    /// <remarks>
    /// Quotes and backslashes are how Todoist lets a name carry a character the grammar would
    /// otherwise take for its own — <c>/"Wed &amp; Thu"</c>, or <c>#Books\&amp;Papers</c>. Both put the
    /// character into the word being built rather than starting a new one, so a name is one token
    /// however it is written.
    ///
    /// Either can be left open, and the caller is told when one is: a query that stops in the
    /// middle of a name is a broken query, not a shorter one.
    /// </remarks>
    /// <param name="query">The query as the account stores it</param>
    /// <param name="unclosed">Whether the query ended part-way through a quote or an escape</param>
    /// <returns>The words and operators, in order</returns>
    private static List<string> Tokenize(string query, out bool unclosed)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var quoted = false;
        var escaped = false;

        foreach (var c in query)
        {
            if (tokens.Count > MaxTokens)
                break;

            if (escaped)
            {
                word.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                word.Append(c);
                continue;
            }

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
        unclosed = quoted || escaped;
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
