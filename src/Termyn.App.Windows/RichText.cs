using System.Text;

namespace Termyn.App.Windows;

/// <summary>
/// The rich text document a description is handed to its control as, whichever half of the panel
/// is drawing it.
/// </summary>
/// <remarks>
/// Both halves build the whole document and hand it over in one message, rather than selecting each
/// run and styling it where it sits. Run by run, a full-length description is thousands of round
/// trips to the control, each of them reflowing it, and measured two orders of magnitude slower.
///
/// The two are drawing the same description, so the header is written here once and they can't
/// drift apart about which face is which or what colour a muted run is.
/// </remarks>
internal static class RichText
{
    /// <summary>The body face's place in the font table.</summary>
    internal const int BodyFace = 0;

    /// <summary>The fixed-width face's place in the font table.</summary>
    internal const int FixedFace = 1;

    /// <summary>The ordinary text colour's place in the colour table.</summary>
    internal const int TextColour = 1;

    /// <summary>The muted colour's place in the colour table.</summary>
    internal const int MutedColour = 2;

    /// <summary>The accent colour's place in the colour table.</summary>
    internal const int AccentColour = 3;

    /// <summary>
    /// Starts a document: its fonts and colours, declared once for every run to name.
    /// </summary>
    /// <remarks>
    /// Declared rather than spelled out on each run, which is what keeps a document proportional to
    /// the text rather than to the number of things in it.
    /// </remarks>
    /// <param name="capacity">Roughly how long the document will be, so it isn't grown a piece at a time</param>
    /// <param name="face">The family the body text is set in</param>
    /// <param name="theme">The colours to draw in</param>
    /// <returns>The document so far, still open for its runs and the closing brace</returns>
    internal static StringBuilder Open(int capacity, string face, Theme theme)
    {
        var rtf = new StringBuilder(capacity);
        rtf.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil ")
           .Append(face)
           .Append(@";}{\f1\fmodern ")
           .Append(Faces.FixedWidth)
           .Append(@";}}");

        return rtf.Append(@"{\colortbl ;")
                  .Append(Colour(theme.Text))
                  .Append(Colour(theme.Muted))
                  .Append(Colour(theme.Accent))
                  .Append('}');
    }

    private static string Colour(Color colour)
        => $@"\red{colour.R}\green{colour.G}\blue{colour.B};";

    /// <summary>
    /// Writes text into a rich text document without any of it being read as instructions.
    /// </summary>
    /// <remarks>
    /// A description is account data and can hold anything. A brace or a backslash left alone would
    /// be read as the document's own syntax — at best drawing the rest of the description wrongly,
    /// at worst swallowing it. Anything outside ASCII goes as its code point, since the header says
    /// this document is ANSI and a pasted em dash or emoji would otherwise arrive as mojibake.
    ///
    /// One character in always comes out as one character in the box, because the rendered view
    /// counts what it writes rather than asking, and every offset after a miscount is wrong. So
    /// every line ending is one paragraph mark, which is how the control holds one — a return and
    /// newline together, a newline on its own and a return on its own alike, since a description
    /// that reaches the view unparsed arrives with whichever the account sent. And the handful of
    /// characters the control won't hold as themselves go in as a space: the specials at the top of
    /// the range, the replacement character among them, are dropped from a document where typing
    /// one in gets a space, and a nought ends the document where it stands.
    /// </remarks>
    /// <param name="rtf">The document to write into</param>
    /// <param name="text">The text, exactly as it should read</param>
    internal static void Escape(StringBuilder rtf, ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\' or '{' or '}':
                    rtf.Append('\\').Append(c);
                    break;

                // The newline after it ends the line, so the return adds nothing of its own.
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    break;

                case '\r' or '\n':
                    rtf.Append(@"\par ");
                    break;

                case '\t':
                    rtf.Append(@"\tab ");
                    break;

                case '\0' or >= '\uFFF9':
                    rtf.Append(' ');
                    break;

                case < (char)128:
                    rtf.Append(c);
                    break;

                default:
                    // Signed, as the format asks: anything above 32767 is written as a negative.
                    rtf.Append(@"\u").Append((short)c).Append('?');
                    break;
            }
        }
    }
}
