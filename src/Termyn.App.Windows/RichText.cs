using System.Text;

namespace Termyn.App.Windows;

/// <summary>
/// The rich text document a description is handed to its control as, whichever half of the panel
/// is drawing it.
/// </summary>
/// <remarks>
/// Both halves build the whole document and hand it over in one message, rather than selecting each
/// run and styling it where it sits. Run by run, a full-length description is thousands of round
/// trips to the control, each of them reflowing it, and it measured eighty times slower.
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
    ///
    /// The face wants its name in no particular language — <see cref="FontFamily.GetName"/> with
    /// nought — rather than in the one the interface is in. A document says it's ANSI, and a face
    /// named in Japanese would reach the control as question marks and be swapped for another.
    /// </remarks>
    /// <param name="rtf">The document to start, which should be empty</param>
    /// <param name="face">The family the body text is set in</param>
    /// <param name="theme">The colours to draw in</param>
    internal static void Open(StringBuilder rtf, string face, Theme theme)
    {
        rtf.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil ");
        Name(rtf, face);
        rtf.Append(@";}{\f1\fmodern ");
        Name(rtf, Faces.FixedWidth);
        rtf.Append(@";}}");

        rtf.Append(@"{\colortbl ;")
           .Append(Colour(theme.Text))
           .Append(Colour(theme.Muted))
           .Append(Colour(theme.Accent))
           .Append('}');
    }

    /// <summary>
    /// Writes a face's name into the font table without any of it being read as the table's syntax.
    /// </summary>
    /// <remarks>
    /// An entry ends at a semicolon, so one in the name is written as its code instead. The table
    /// won't take a character's code point the way the text does — the name spills out into the
    /// description and takes the rest of the table with it — so anything outside ASCII becomes a
    /// question mark, which at worst draws in the control's default face.
    /// </remarks>
    /// <param name="rtf">The document to write into</param>
    /// <param name="face">The name, exactly as it's installed</param>
    private static void Name(StringBuilder rtf, string face)
    {
        foreach (var c in face)
        {
            switch (c)
            {
                case '\\' or '{' or '}':
                    rtf.Append('\\').Append(c);
                    break;

                case ';':
                    rtf.Append(@"\'3b");
                    break;

                case < ' ' or > '~':
                    rtf.Append('?');
                    break;

                default:
                    rtf.Append(c);
                    break;
            }
        }
    }

    private static string Colour(Color colour)
        => $@"\red{colour.R}\green{colour.G}\blue{colour.B};";

    /// <summary>
    /// Hands a finished document to a control, replacing whatever it held.
    /// </summary>
    /// <remarks>
    /// Loaded as a stream rather than assigned to <see cref="RichTextBox.Rtf"/>, which first streams
    /// the control's whole current document back out to see whether the new one is the same — a
    /// second pass over the text on every redraw, for an answer that's always no. Everything written
    /// here is ASCII, the rest having gone as code points, so that's the encoding it goes in.
    /// </remarks>
    /// <param name="box">The control to load it into</param>
    /// <param name="rtf">The document, closing brace and all</param>
    internal static void Load(RichTextBox box, StringBuilder rtf)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf.ToString()));
        box.LoadFile(stream, RichTextBoxStreamType.RichText);
    }

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
    /// one in gets a space; so is half of a surrogate pair without the other half; and a nought ends
    /// the document where it stands.
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
                case >= '\uD800' and <= '\uDFFF' when Unpaired(text, i):
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

    /// <summary>Whether the surrogate at <paramref name="i"/> is missing the other half of its pair.</summary>
    /// <param name="text">The text it's in</param>
    /// <param name="i">Where it is</param>
    /// <returns>True for a half with nothing to pair with</returns>
    private static bool Unpaired(ReadOnlySpan<char> text, int i)
        => char.IsHighSurrogate(text[i])
            ? i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])
            : i == 0 || !char.IsHighSurrogate(text[i - 1]);
}
