using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Termyn.Core;

namespace Termyn.Presentation;

/// <summary>
/// What a stretch of markdown source is, for something drawing it.
/// </summary>
/// <remarks>
/// The source rather than a rendering of it: this describes the text the account holds, markers and
/// all, so that a box you type into can show what the markdown means without changing a character
/// of what it says.
/// </remarks>
public enum MarkdownStyle : byte
{
    /// <summary>Body text, and anything with nothing particular to say about it.</summary>
    Text = 0,

    /// <summary>Syntax — a hash, a bullet, a pair of asterisks, a bracket. Drawn quietly.</summary>
    Marker,

    Heading1,
    Heading2,
    Heading3,
    Heading4,
    Heading5,
    Heading6,

    Strong,
    Emphasis,
    Struck,

    /// <summary>Inline code, or a fenced or indented block of it.</summary>
    Code,

    /// <summary>The words of a link — the part a reader is meant to read.</summary>
    LinkText,

    /// <summary>The address of a link, which is there to be edited rather than read.</summary>
    Url,

    /// <summary>Quoted text, after its marker.</summary>
    Quote,

    /// <summary>A thematic break.</summary>
    Rule,
}

/// <summary>A stretch of the source that is all one thing.</summary>
/// <param name="Start">Where it begins, as an offset into the markdown</param>
/// <param name="Length">How many characters it covers</param>
/// <param name="Style">What to draw it as</param>
public readonly record struct MarkdownRun(int Start, int Length, MarkdownStyle Style);

/// <summary>
/// Reads markdown and says how each part of it should be drawn.
/// </summary>
/// <remarks>
/// Here rather than in the window, and framework-free, because it is the same answer whatever draws
/// it — which is also what lets it be tested without a control to look at. The two things that have
/// wanted it draw in quite different ways and agree about every character, which is the point.
///
/// The runs tile the source: every character is in exactly one, they are in order, and they don't
/// overlap. That is what a caller needs to walk them straight through without deciding anything.
/// </remarks>
public static class MarkdownHighlight
{
    /// <summary>
    /// The same grammar the rendered view reads, so a description can't mean one thing written and
    /// another read. Precise source locations because every answer here is an offset.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UsePreciseSourceLocation()
        .Build();

    /// <summary>The deepest heading there is a style for. Anything below it is drawn as that.</summary>
    private const int MaxHeading = 6;

    /// <summary>
    /// Where each part of some markdown begins and what it is.
    /// </summary>
    /// <remarks>
    /// Built as a style per character and then joined up, rather than as spans laid over each
    /// other. Markdown nests — bold inside a heading, code inside a link — and spans that overlap
    /// leave the caller to work out which of them wins. Writing the outer one first and letting the
    /// inner overwrite it settles that here, once, where it can be tested. A description tops out
    /// at sixteen thousand characters, so the array costs nothing worth counting.
    /// </remarks>
    /// <param name="markdown">The source to read</param>
    /// <returns>Runs covering the whole of it, in order</returns>
    public static IReadOnlyList<MarkdownRun> Runs(string? markdown)
    {
        var text = markdown ?? string.Empty;
        if (text.Length == 0)
            return [];

        var styles = new MarkdownStyle[text.Length];

        foreach (var block in Parse(text))
            Paint(block, styles, text);

        return Join(styles);
    }

    /// <summary>
    /// The blocks of the markdown, or none at all when it can't be read.
    /// </summary>
    /// <remarks>
    /// Markdig refuses input nested past its own limit by throwing, and a description arrives by
    /// sync rather than only by typing — so this is reachable without anyone having done anything
    /// wrong. Unhighlighted is a fine answer; taking the window down is not.
    /// </remarks>
    private static IEnumerable<Block> Parse(string text)
    {
        try
        {
            return Markdown.Parse(text, Pipeline);
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private static void Paint(Block block, MarkdownStyle[] styles, string text)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var level = Math.Clamp(heading.Level, 1, MaxHeading);
                Fill(styles, heading.Span, (MarkdownStyle)((byte)MarkdownStyle.Heading1 + level - 1));

                // The hashes are syntax, not part of the words. Drawn quietly so the heading reads
                // as a heading and the marker stays visible for whoever has to edit it.
                Fill(styles, heading.Span.Start, LeadingRun(text, heading.Span.Start, '#'), MarkdownStyle.Marker);
                PaintInlines(heading.Inline, styles, text);
                break;

            case ParagraphBlock paragraph:
                PaintInlines(paragraph.Inline, styles, text);
                break;

            case QuoteBlock quote:
                Fill(styles, quote.Span, MarkdownStyle.Quote);
                MarkLineStarts(styles, text, quote.Span, '>');
                foreach (var child in quote)
                    Paint(child, styles, text);
                break;

            case ListBlock list:
                foreach (var item in list)
                {
                    if (item is not ListItemBlock listItem)
                        continue;

                    // Whatever introduces the item — "- ", "* ", "12. ". Taken from the source
                    // rather than rebuilt from the model, so it is the marker actually written.
                    Fill(styles, listItem.Span.Start, MarkerRun(text, listItem.Span.Start), MarkdownStyle.Marker);

                    foreach (var child in listItem)
                        Paint(child, styles, text);
                }

                break;

            case CodeBlock code:
                Fill(styles, code.Span, MarkdownStyle.Code);
                break;

            case HtmlBlock html:
                Fill(styles, html.Span, MarkdownStyle.Code);
                break;

            case ThematicBreakBlock rule:
                Fill(styles, rule.Span, MarkdownStyle.Rule);
                break;

            case ContainerBlock container:
                foreach (var child in container)
                    Paint(child, styles, text);
                break;
        }
    }

    private static void PaintInlines(ContainerInline? inlines, MarkdownStyle[] styles, string text)
    {
        if (inlines is null)
            return;

        foreach (var inline in inlines)
            PaintInline(inline, styles, text);
    }

    private static void PaintInline(Inline inline, MarkdownStyle[] styles, string text)
    {
        switch (inline)
        {
            case EmphasisInline emphasis:
                var style = emphasis.DelimiterChar switch
                {
                    '~' => MarkdownStyle.Struck,
                    _ => emphasis.DelimiterCount >= 2 ? MarkdownStyle.Strong : MarkdownStyle.Emphasis,
                };

                Fill(styles, emphasis.Span, style);

                // The delimiters at each end, quietly. They are the same characters at both ends
                // and the count is what says which kind this is.
                Fill(styles, emphasis.Span.Start, emphasis.DelimiterCount, MarkdownStyle.Marker);
                Fill(styles, emphasis.Span.End - emphasis.DelimiterCount + 1, emphasis.DelimiterCount, MarkdownStyle.Marker);

                foreach (var child in emphasis)
                    PaintInline(child, styles, text);
                break;

            case CodeInline code:
                Fill(styles, code.Span, MarkdownStyle.Code);
                Fill(styles, code.Span.Start, code.DelimiterCount, MarkdownStyle.Marker);
                Fill(styles, code.Span.End - code.DelimiterCount + 1, code.DelimiterCount, MarkdownStyle.Marker);
                break;

            case LinkInline link:
                PaintLink(link, styles, text);
                break;

            // The angle-bracket form. Its own case because it is a leaf rather than a container,
            // and because Lexilla's markdown lexer misses it entirely — which is half of why the
            // highlighting is ours wherever it ends up being drawn.
            case AutolinkInline auto:
                Fill(styles, auto.Span, MarkdownStyle.LinkText);
                Fill(styles, auto.Span.Start, 1, MarkdownStyle.Marker);
                Fill(styles, auto.Span.End, 1, MarkdownStyle.Marker);
                break;

            // "- [ ] a thing". A marker, and emphatically not a link: read as one it is drawn as
            // something to click that then does nothing, on the commonest shape of description
            // there is.
            case TaskList task:
                Fill(styles, task.Span, MarkdownStyle.Marker);
                break;

            case ContainerInline container:
                foreach (var child in container)
                    PaintInline(child, styles, text);
                break;
        }
    }

    /// <summary>
    /// Draws a link: its words as words, its address as an address, its punctuation as neither.
    /// </summary>
    /// <remarks>
    /// A bare URL the autolink extension found is the whole span and has no punctuation to hide, so
    /// it is left as one run. A written link is picked apart from the source rather than from the
    /// model's label and URL spans, which don't agree with each other across the reference forms.
    ///
    /// Only coloured as a link where it is somewhere worth going. A <c>file:</c> or
    /// <c>javascript:</c> target drawn in the link colour looks like something to click and then
    /// isn't, which is the same judgement the rendered view makes.
    /// </remarks>
    private static void PaintLink(LinkInline link, MarkdownStyle[] styles, string text)
    {
        var openable = Links.External(link.Url) is not null;

        if (link.IsAutoLink)
        {
            Fill(styles, link.Span, openable ? MarkdownStyle.LinkText : MarkdownStyle.Text);
            return;
        }

        var span = link.Span;
        var close = text.IndexOf(']', span.Start);
        var end = span.End;

        if (close < 0 || close > end)
        {
            Fill(styles, span, MarkdownStyle.Text);
            return;
        }

        // "[words](address)" — the brackets and parentheses quiet, the words coloured when there is
        // somewhere to go, the address quiet either way.
        Fill(styles, span.Start + 1, close - span.Start - 1, openable ? MarkdownStyle.LinkText : MarkdownStyle.Text);
        Fill(styles, span.Start, 1, MarkdownStyle.Marker);
        Fill(styles, close, 1, MarkdownStyle.Marker);
        Fill(styles, close + 1, 1, MarkdownStyle.Marker);
        Fill(styles, close + 2, end - close - 2, MarkdownStyle.Url);
        Fill(styles, end, 1, MarkdownStyle.Marker);

        foreach (var child in link)
            PaintInline(child, styles, text);
    }

    /// <summary>How many of <paramref name="marker"/> run from <paramref name="at"/>, plus a space.</summary>
    private static int LeadingRun(string text, int at, char marker)
    {
        var length = 0;
        while (at + length < text.Length && text[at + length] == marker)
            length++;

        if (at + length < text.Length && text[at + length] == ' ')
            length++;

        return length;
    }

    /// <summary>The list marker written at <paramref name="at"/> — "- ", "* ", "12. ".</summary>
    private static int MarkerRun(string text, int at)
    {
        var length = 0;
        while (at + length < text.Length && char.IsAsciiDigit(text[at + length]))
            length++;

        if (at + length < text.Length && (length > 0 ? text[at + length] is '.' or ')' : text[at + length] is '-' or '*' or '+'))
            length++;
        else if (length > 0)
            return 0;

        if (length == 0)
            return 0;

        if (at + length < text.Length && text[at + length] == ' ')
            length++;

        return length;
    }

    /// <summary>Marks the character starting each line of a span, where it is the one expected.</summary>
    private static void MarkLineStarts(MarkdownStyle[] styles, string text, SourceSpan span, char marker)
    {
        var at = span.Start;
        while (at <= span.End && at < text.Length)
        {
            // Past the indent, since a nested quote's markers sit in from the margin.
            var start = at;
            while (start < text.Length && text[start] is ' ' or '\t')
                start++;

            if (start < text.Length && text[start] == marker)
                Fill(styles, start, text.ElementAtOrDefault(start + 1) == ' ' ? 2 : 1, MarkdownStyle.Marker);

            var next = text.IndexOf('\n', at);
            if (next < 0)
                return;

            at = next + 1;
        }
    }

    private static void Fill(MarkdownStyle[] styles, SourceSpan span, MarkdownStyle style)
        => Fill(styles, span.Start, span.Length, style);

    private static void Fill(MarkdownStyle[] styles, int start, int length, MarkdownStyle style)
    {
        // Clamped rather than trusted. A span can reach past the text it was parsed from — the
        // trailing newline of a block that ended the description is the everyday one — and a
        // highlighter that threw would take the window down over a description somebody synced.
        var from = Math.Max(start, 0);
        var to = Math.Min(start + length, styles.Length);

        for (var i = from; i < to; i++)
            styles[i] = style;
    }

    /// <summary>Joins a style per character into the runs a caller walks.</summary>
    private static List<MarkdownRun> Join(MarkdownStyle[] styles)
    {
        var runs = new List<MarkdownRun>();
        var start = 0;

        for (var i = 1; i <= styles.Length; i++)
        {
            if (i < styles.Length && styles[i] == styles[start])
                continue;

            runs.Add(new MarkdownRun(start, i - start, styles[start]));
            start = i;
        }

        return runs;
    }
}
