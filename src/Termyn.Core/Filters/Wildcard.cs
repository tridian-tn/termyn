namespace Termyn.Core.Filters;

/// <summary>
/// The asterisk Todoist reads in a name as "anything here": <c>%home*</c> for every label starting
/// "home", <c>#*Work</c> for every project ending "Work", <c>/*Work*</c> for every section with it
/// anywhere in the name — and <c>/*</c> on its own for any section at all.
/// </summary>
/// <remarks>
/// Every asterisk in a name is one, escaped or quoted too. Todoist's help gives no way of writing
/// one that isn't, and a name that really has one in it still fits itself.
/// </remarks>
internal static class Wildcard
{
    /// <summary>Whether a name as a query wrote it stands for any name that fits it.</summary>
    /// <param name="written">The name in the query</param>
    /// <returns>Whether it has an asterisk in it</returns>
    public static bool In(string written) => written.Contains('*');

    /// <summary>
    /// Whether a name in the account answers to a name as a query wrote it.
    /// </summary>
    /// <remarks>
    /// Without an asterisk that's the name itself, in any case, as it always was. With one, the
    /// pieces either side have to be found in order, the first at the start and the last at the end.
    /// Each piece in between is taken at the first place it fits, which is never worse than a later
    /// one — it leaves more of the name for the pieces after it — so there's nothing to go back and
    /// try again, however the query was written.
    /// </remarks>
    /// <param name="written">The name in the query, asterisks and all</param>
    /// <param name="name">A name in the account</param>
    /// <returns>Whether it's that name, or fits it</returns>
    public static bool Matches(string written, string name)
    {
        if (!In(written))
            return string.Equals(written, name, StringComparison.OrdinalIgnoreCase);

        var pieces = written.Split('*');
        var first = pieces[0];
        var last = pieces[^1];

        // Long enough for both ends without them sharing a character: "ab*ba" doesn't fit "aba".
        if (name.Length < first.Length + last.Length
            || !name.StartsWith(first, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(last, StringComparison.OrdinalIgnoreCase))
            return false;

        var at = first.Length;
        var end = name.Length - last.Length;

        foreach (var piece in pieces.AsSpan(1, pieces.Length - 2))
        {
            var found = name.AsSpan(at, end - at).IndexOf(piece, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;

            at += found + piece.Length;
        }

        return true;
    }
}
