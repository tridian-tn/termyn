namespace Termyn.Presentation;

/// <summary>Which list an entry came from, so the view can group or icon them.</summary>
public enum PaletteKind
{
    Action,
    SmartView,
    Project,
    Section,
    Label,
    Filter,
}

/// <summary>
/// One row of the command palette. Either it navigates (<paramref name="Selection"/>) or it runs a
/// command (<paramref name="Command"/>) — the view dispatches, because most commands end in a dialog.
/// </summary>
/// <remarks>
/// The command is an <see cref="AppCommand"/>, the same one the menus raise, so a palette entry and
/// a menu entry for the same action are the same action rather than two spellings of it.
/// </remarks>
public sealed record PaletteEntry(
    PaletteKind Kind,
    string Label,
    string Hint,
    ViewSelection? Selection = null,
    AppCommand Command = AppCommand.None);

/// <summary>
/// Ranks palette entries against what the user has typed. Matching is a subsequence — "npr" finds
/// "New project" — so a few letters from anywhere in the name are enough to reach it.
/// </summary>
public static class Fuzzy
{
    private const int StartOfWordBonus = 12;
    private const int ConsecutiveBonus = 8;
    private const int GapPenalty = 1;

    /// <summary>
    /// Scores <paramref name="candidate"/> against <paramref name="query"/>, higher being better.
    /// Null when the query isn't a subsequence of the candidate at all.
    /// </summary>
    public static int? Score(string candidate, string query)
    {
        if (query.Length == 0)
            return 0;
        if (candidate.Length == 0)
            return null;

        var score = 0;
        var q = 0;
        var previousMatched = -2;

        for (var i = 0; i < candidate.Length && q < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[q]))
                continue;

            // A letter opening a word is what people reach for when they abbreviate, so "np" should
            // rank "New project" above a run of the same two letters buried inside one word.
            if (i == 0 || !char.IsLetterOrDigit(candidate[i - 1]))
                score += StartOfWordBonus;

            if (i == previousMatched + 1)
                score += ConsecutiveBonus;
            else
                score -= Math.Min(i - previousMatched - 1, 10) * GapPenalty;

            previousMatched = i;
            q++;
        }

        // Not every character of the query was placed, so this candidate doesn't contain it.
        if (q < query.Length)
            return null;

        // Among equally-matching candidates the shorter one is the more specific.
        return score - candidate.Length / 4;
    }

    /// <summary>
    /// Orders entries by how well they match, dropping the ones that don't. An empty query keeps
    /// everything in the order it was given, which is the palette's own idea of usefulness.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> Rank(IEnumerable<PaletteEntry> entries, string? query, int limit = 60)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return entries.Take(limit).ToList();

        return entries
            .Select(e => (Entry: e, Score: Best(e, trimmed)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            // Stable enough to read: same score, same order every time it is typed.
            .ThenBy(x => x.Entry.Label.Length)
            .ThenBy(x => x.Entry.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>
    /// The better of the label and the hint. A project is reachable by its own name or by the word
    /// "project", without the hint's match ever outranking a real name match on the label.
    /// </summary>
    private static int? Best(PaletteEntry entry, string query)
    {
        var label = Score(entry.Label, query);
        var hint = Score(entry.Hint, query) is { } h ? h - StartOfWordBonus : (int?)null;
        return (label, hint) switch
        {
            ({ } a, { } b) => Math.Max(a, b),
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
    }
}
