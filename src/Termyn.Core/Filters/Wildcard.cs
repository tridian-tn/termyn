using System.Text;

namespace Termyn.Core.Filters;

/// <summary>
/// A name as a query wrote it, which Todoist lets stand for every name it fits with an asterisk:
/// <c>%home*</c> for every label starting "home", <c>#*Work</c> for every project ending "Work",
/// <c>/*Work*</c> for every section with it anywhere in the name — and <c>/*</c> on its own for
/// any section at all.
/// </summary>
/// <remarks>
/// An asterisk that was escaped or quoted is the character itself, the way a backslash or quotes
/// hand any other character the grammar would take for its own to the name. The tokenizer passes
/// those on with their backslash, and a backslash of the name's own as two, so in a name a
/// backslash only ever comes before one of those two and means it literally.
/// </remarks>
internal sealed class Wildcard
{
    /// <summary>The pieces between the asterisks, as the characters they stand for.</summary>
    private readonly string[] _pieces;

    private Wildcard(string[] pieces)
    {
        _pieces = pieces;
        Length = pieces.Sum(p => p.Length);
    }

    /// <summary>Whether it stands for any name that fits it, rather than for one name.</summary>
    public bool IsPattern => _pieces.Length > 1;

    /// <summary>The one name it stands for, when it isn't a pattern.</summary>
    public string Name => _pieces[0];

    /// <summary>The fewest characters a name can have and still fit it.</summary>
    public int Length { get; }

    /// <summary>Reads a name as a query wrote it.</summary>
    /// <param name="written">The name, as the tokenizer passed it on</param>
    /// <returns>What it stands for</returns>
    public static Wildcard Of(string written)
    {
        var pieces = new List<string>();
        var piece = new StringBuilder();

        for (var i = 0; i < written.Length; i++)
        {
            if (written[i] == '\\' && i + 1 < written.Length)
                piece.Append(written[++i]);
            else if (written[i] == '*')
            {
                pieces.Add(piece.ToString());
                piece.Clear();
            }
            else
                piece.Append(written[i]);
        }

        pieces.Add(piece.ToString());
        return new Wildcard([.. pieces]);
    }

    /// <summary>Whether a name is nothing but asterisks, and so fits any name at all.</summary>
    /// <param name="written">The name, as the tokenizer passed it on</param>
    /// <returns>Whether it's only asterisks</returns>
    public static bool FitsAnything(string written)
        => written.Length > 0 && written.AsSpan().TrimStart('*').IsEmpty;

    /// <summary>Text as the tokenizer passed it on, with the backslashes it kept taken back out.</summary>
    /// <param name="written">The text, as the tokenizer passed it on</param>
    /// <returns>The characters it stands for, asterisks and all</returns>
    public static string Unescaped(string written)
    {
        if (!written.Contains('\\'))
            return written;

        var text = new StringBuilder(written.Length);
        for (var i = 0; i < written.Length; i++)
            text.Append(written[i] == '\\' && i + 1 < written.Length ? written[++i] : written[i]);

        return text.ToString();
    }

    /// <summary>
    /// Whether a name in the account answers to this one.
    /// </summary>
    /// <remarks>
    /// Without an asterisk that's the name itself, in any case, as it always was. With one, the
    /// pieces either side have to be found in order, the first at the start and the last at the end.
    /// Each piece in between is taken at the first place it fits, which is never worse than a later
    /// one — it leaves more of the name for the pieces after it — so there's nothing to go back and
    /// try again, however the query was written.
    /// </remarks>
    /// <param name="name">A name in the account</param>
    /// <returns>Whether it's that name, or fits it</returns>
    public bool Matches(string name)
    {
        if (!IsPattern)
            return string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

        var first = _pieces[0];
        var last = _pieces[^1];

        // Long enough for both ends without them sharing a character: "ab*ba" doesn't fit "aba".
        if (name.Length < first.Length + last.Length
            || !name.StartsWith(first, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(last, StringComparison.OrdinalIgnoreCase))
            return false;

        var at = first.Length;
        var end = name.Length - last.Length;

        foreach (var piece in _pieces.AsSpan(1, _pieces.Length - 2))
        {
            var found = name.AsSpan(at, end - at).IndexOf(piece, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;

            at += found + piece.Length;
        }

        return true;
    }
}
