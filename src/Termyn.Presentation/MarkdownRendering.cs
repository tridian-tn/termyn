using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Termyn.Core;

namespace Termyn.Presentation;

/// <summary>
/// How a run of a rendered description is drawn.
/// </summary>
/// <param name="Larger">
/// How much bigger than the body text, as a fraction of it — so nought is the body size. Held that
/// way round deliberately: a struct can be made without its primary constructor ever running, and a
/// scale that defaulted to zero would then ask for a font of no size at all
/// </param>
/// <param name="Indent">How many levels in the paragraph sits</param>
/// <param name="Hanging">
/// Whether the paragraph's lines after its first sit one level further in than the first, which is
/// how a list item's words hang in from its bullet
/// </param>
/// <param name="Tight">Whether the paragraph goes without the air a paragraph usually has under it</param>
public readonly record struct RenderedStyle(
    bool Bold = false,
    bool Italic = false,
    bool Strike = false,
    bool Fixed = false,
    bool Muted = false,
    float Larger = 0f,
    int Indent = 0,
    bool Hanging = false,
    bool Tight = false)
{
    /// <summary>Body text, with nothing about it changed.</summary>
    public static readonly RenderedStyle Plain = default;
}

/// <summary>
/// One run of a rendered description: some text, drawn one way.
/// </summary>
/// <param name="Text">The words, exactly as they read. Can hold line endings of its own</param>
/// <param name="Style">How they're drawn</param>
/// <param name="EndsLine">Whether a line ending follows them</param>
/// <param name="Source">
/// Where in the markdown they were written and how much of it they were written with, or null for a
/// run the markdown doesn't contain — a bullet, the empty run that closes a paragraph
/// </param>
/// <param name="Link">
/// Where the run points when it's part of a link that would be followed, or null. A link to
/// anywhere that wouldn't be — a file, a script — is drawn as plain text, and has none
/// </param>
public sealed record RenderedRun(
    string Text,
    RenderedStyle Style,
    bool EndsLine,
    (int Start, int Length)? Source = null,
    string? Link = null);

/// <summary>
/// A description as it reads rather than as it's written: the runs it's drawn as, and the way back
/// from the drawing to the markdown behind it.
/// </summary>
/// <remarks>
/// The rendering's half of what <see cref="MarkdownHighlight"/> is for the markdown as typed, and
/// here for the same reason: what each run says, how it's styled and where it came from is the same
/// answer whatever draws it. Drawing the runs is all that's left to a control.
///
/// Offsets into the rendering count a line ending as one character, whether it was written as a
/// return and newline, a newline, or a return alone. That's how the text reads once drawn, and how a
/// rich edit control holds it.
/// </remarks>
public sealed class MarkdownRendering
{
    /// <summary>
    /// The grammar the markdown is written in, and one thing more.
    /// </summary>
    /// <remarks>
    /// Markdown proper reads a single newline as a space and wants a blank line or two trailing
    /// spaces before it will break a line. Todoist doesn't: press Return once there and the line
    /// breaks, which is how the descriptions in an account are already written. Following the spec
    /// here would run those lines together and be right about nothing anybody typed. The editor
    /// keeps the newline as it was typed, and has no line to break.
    /// </remarks>
    private static readonly MarkdownPipeline Pipeline = MarkdownHighlight.Grammar()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>Nothing at all, before there's been a description to render.</summary>
    public static readonly MarkdownRendering Empty = new([]);

    private readonly List<RenderedRun> _runs;

    /// <summary>
    /// Where each link sits in the rendering and where it points.
    /// </summary>
    /// <remarks>
    /// The rendering shows a link's words and not its address, so once it's drawn there is nothing
    /// left in the text to open. This is what a click looks the address up in, and what the pointer
    /// asks on every move.
    /// </remarks>
    private readonly List<(int Start, int End, string Url)> _links = [];

    /// <summary>
    /// Where each run of the rendering came from in the markdown behind it.
    /// </summary>
    /// <remarks>
    /// The rendering drops the markers and reorders nothing, so a rendered offset has a markdown
    /// offset under it — but only the walk knows which, since once it's drawn the syntax it was
    /// written with is gone. This is what a click looks that up in, so opening the text to type into
    /// it lands the caret where the user was pointing rather than at the top.
    /// </remarks>
    private readonly List<(int Start, int Length, int Source, int SourceLength)> _sources = [];

    private MarkdownRendering(List<RenderedRun> runs)
    {
        _runs = runs;

        var at = 0;
        foreach (var run in runs)
        {
            var shown = Shown(run.Text);
            var length = shown + (run.EndsLine ? 1 : 0);

            if (run.Link is { } url && length > 0)
                _links.Add((at, at + length, url));

            // A run's own line ending isn't part of what it maps: the markdown behind it may not have
            // one there at all.
            if (run is { Source: { Length: > 0 } source, Text.Length: > 0 })
                _sources.Add((at, shown, source.Start, source.Length));

            at += length;
        }

        Length = at;
    }

    /// <summary>The runs, in the order they're drawn.</summary>
    public IReadOnlyList<RenderedRun> Runs => _runs;

    /// <summary>How many characters the rendering holds, a line ending counting as one.</summary>
    public int Length { get; }

    /// <summary>
    /// The rendering as text, with every line ending a newline.
    /// </summary>
    /// <remarks>
    /// The same length as <see cref="Length"/> says, character for character, so an offset into this
    /// is an offset into the rendering. Only returns and newlines are folded, which is what a line
    /// ending is to <see cref="Shown"/>.
    /// </remarks>
    public string Text => _text ??= string.Concat(_runs.Select(r =>
        r.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') + (r.EndsLine ? "\n" : string.Empty)));

    /// <summary>The text, once it's been asked for. A rendering never changes after it's built.</summary>
    private string? _text;

    /// <summary>
    /// Renders a description.
    /// </summary>
    /// <remarks>
    /// The markdown as it was written when it can't be drawn. Markdig refuses input nested past its
    /// own limit — a hundred and twenty-eight quote markers, or sixty-four list levels — by throwing,
    /// and anything else the walk trips over gets the same answer. A description is account data: it
    /// arrives by sync from the web app or another device, so none of this is only reachable by
    /// typing, and letting it out would take down the window on the next publish with the offending
    /// task selected. Worse, the box that would let the user fix the text is the one that throws.
    /// The raw text is a truthful thing to show and always readable.
    ///
    /// Written again from the start rather than finished from where it stopped, so the runs and
    /// what's said about them describe the same rendering either way.
    /// </remarks>
    /// <param name="markdown">The description, as the account holds it</param>
    /// <returns>Its rendering</returns>
    public static MarkdownRendering Of(string markdown)
    {
        var walk = new Walk();
        try
        {
            foreach (var block in Markdown.Parse(markdown, Pipeline))
                walk.Block(block, indent: 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AsWritten(markdown);
        }

        return new MarkdownRendering(walk.Runs);
    }

    /// <summary>
    /// The markdown exactly as it was written, for when it can't be drawn any other way.
    /// </summary>
    /// <remarks>
    /// What <see cref="Of"/> falls back to when the markdown can't be parsed, and what something
    /// drawing a rendering can fall back to when drawing it fails — the same truthful, always
    /// readable answer either way.
    /// </remarks>
    /// <param name="markdown">The description, as the account holds it</param>
    /// <returns>Its text as it stands, a run a line</returns>
    public static MarkdownRendering AsWritten(string markdown)
    {
        var walk = new Walk();
        walk.Plain(markdown);

        return new MarkdownRendering(walk.Runs);
    }

    /// <summary>The link at a point in the rendering, or null where there is none.</summary>
    /// <param name="index">An offset into the rendering</param>
    /// <returns>Where the link there goes, or null</returns>
    public string? LinkAt(int index)
    {
        foreach (var (start, end, url) in _links)
            if (index >= start && index < end)
                return url;

        return null;
    }

    /// <summary>
    /// Where in the markdown the rendered text at this index was written.
    /// </summary>
    /// <remarks>
    /// An index inside a run maps straight through, since a run is drawn from its source in order.
    /// An index in something the markdown doesn't contain — a bullet, the gap after a paragraph —
    /// belongs to no run, and answers with the start of the next one rather than the end of the
    /// last: the text after a bullet is what a click on the bullet was aiming at.
    /// </remarks>
    /// <param name="index">An offset into the rendering</param>
    /// <returns>The offset into the markdown, or zero when there is nothing to map</returns>
    public int SourceAt(int index)
    {
        var after = -1;

        foreach (var (start, length, source, sourceLength) in _sources)
        {
            // Clamped against the source's length, not the run's: a run of code is shorter than the
            // backticks it was written with, and a rule is longer than the three dashes that made it.
            if (index >= start && index < start + length)
                return source + Math.Min(index - start, sourceLength - 1);

            if (start > index && after < 0)
                after = source;
        }

        if (after >= 0)
            return after;

        // Past everything: the end of the last run, so a click below the text lands at the bottom
        // of the markdown rather than the top of it.
        return _sources.Count > 0
            ? _sources[^1].Source + _sources[^1].SourceLength
            : 0;
    }

    /// <summary>
    /// How many characters of the rendering <paramref name="written"/> takes up.
    /// </summary>
    /// <remarks>
    /// Not its length: a line ending is one character once drawn. A run carries its own endings as
    /// well as the one that closes it — a fenced block arrives as several lines in a single run — so
    /// they're counted rather than assumed to be one.
    ///
    /// It is the pair that collapses and not the return on its own. A return with nothing after it
    /// is a line ending of its own and stays a character, which a description can hold: the parser
    /// tidies the line endings of anything it reads, but markdown nested past what it will take is
    /// passed through exactly as the account sent it.
    /// </remarks>
    /// <param name="written">The text of a run</param>
    /// <returns>How many characters of the rendering it takes up</returns>
    private static int Shown(string written) => written.Length - written.AsSpan().Count("\r\n");

    /// <summary>
    /// The walk from the parsed markdown to runs, kept while one rendering is being built.
    /// </summary>
    private sealed class Walk
    {
        /// <summary>The runs so far.</summary>
        public List<RenderedRun> Runs { get; } = [];

        /// <summary>Where the link being walked goes, or null outside one.</summary>
        private string? _link;

        /// <summary>
        /// Adds the markdown as it was written, a line at a time.
        /// </summary>
        /// <remarks>
        /// A run per line, each noted against where it starts in the markdown, because here the two
        /// are the same characters — so a click still opens the editor where it was aimed rather
        /// than at the top. Per line rather than as one run because a return and newline is one
        /// character once drawn: one run over the lot would put a click a character further back
        /// for every line above it.
        /// </remarks>
        public void Plain(string markdown)
        {
            var start = 0;
            while (start < markdown.Length)
            {
                var end = markdown.IndexOf("\r\n", start, StringComparison.Ordinal);
                end = end < 0 ? markdown.Length : end + 2;

                Add(markdown[start..end], RenderedStyle.Plain, endsLine: false, new SourceSpan(start, end - 1));
                start = end;
            }

            Add(string.Empty, RenderedStyle.Plain);
        }

        public void Block(Block block, int indent)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    // One size for H1 and a smaller one for everything below it: Todoist's editor only
                    // offers two levels, and pasted markdown can go deeper than it is worth drawing.
                    var larger = heading.Level <= 1 ? 0.5f : heading.Level == 2 ? 0.25f : 0.1f;
                    Paragraph(heading.Inline, new RenderedStyle(Bold: true, Larger: larger, Indent: indent));
                    break;

                case ParagraphBlock paragraph:
                    Paragraph(paragraph.Inline, new RenderedStyle(Indent: indent));
                    break;

                case QuoteBlock quote:
                    foreach (var child in quote)
                        Block(child, indent + 1);
                    break;

                case ListBlock list:
                    List(list, indent);
                    break;

                case CodeBlock code:
                    Lines(code, new RenderedStyle(Fixed: true, Muted: true, Indent: indent + 1));
                    break;

                // Not a container, so the fallback below never sees it, and its words vanished
                // entirely. A pasted snippet is worth showing as what it is rather than not at all.
                case HtmlBlock html:
                    Lines(html, new RenderedStyle(Fixed: true, Muted: true, Indent: indent + 1));
                    break;

                case ThematicBreakBlock rule:
                    Add("————————", new RenderedStyle(Muted: true, Indent: indent), from: rule.Span);
                    break;

                case ContainerBlock container:
                    foreach (var child in container)
                        Block(child, indent);
                    break;
            }
        }

        private void List(ListBlock list, int indent)
        {
            var number = list.OrderedStart is { Length: > 0 } start && int.TryParse(start, out var first) ? first : 1;

            foreach (var item in list.OfType<ListItemBlock>())
            {
                var bullet = list.IsOrdered ? $"{number++}. " : "•  ";

                // Every run of the item carries the same paragraph settings, because that is what they
                // are — set per paragraph, not per run, so the last word of a line would otherwise
                // decide the indent for the whole of it.
                var line = new RenderedStyle(Indent: indent, Hanging: true, Tight: true);

                var lead = true;
                foreach (var child in item)
                {
                    // The marker leads the item's first line and the rest follows hanging under it,
                    // which is what makes a wrapped item read as one thing.
                    if (lead && child is ParagraphBlock paragraph)
                    {
                        Add(bullet, line with { Muted = true }, endsLine: false);
                        Paragraph(paragraph.Inline, line);
                        lead = false;
                        continue;
                    }

                    Block(child, indent + 1);
                    lead = false;
                }
            }
        }

        /// <summary>
        /// Adds a block that carries its text as raw lines rather than as inlines.
        /// </summary>
        /// <remarks>
        /// A run a line, each mapped from where that line sits in the markdown. Mapped from the block
        /// as a whole, the fence above a fenced block — written but not drawn — put every click inside
        /// it that many characters early, and an indented block drifted further on each line for the
        /// indent every line loses.
        ///
        /// Blank lines at the bottom and spaces at the end of the last line are left off, so the block
        /// doesn't leave a gap under it that isn't in anything the user wrote to see.
        /// </remarks>
        private void Lines(LeafBlock block, RenderedStyle style)
        {
            // By count rather than by walking the array behind it, which is longer than the lines it
            // holds and isn't there at all for a fence with nothing in it — the first thing on screen
            // after typing three backticks.
            var last = block.Lines.Count - 1;
            while (last >= 0 && string.IsNullOrWhiteSpace(block.Lines.Lines[last].Slice.ToString()))
                last--;

            if (last < 0)
            {
                Add(string.Empty, style, from: block.Span);
                return;
            }

            for (var i = 0; i <= last; i++)
            {
                var slice = block.Lines.Lines[i].Slice;
                var text = slice.ToString();

                Add(i == last ? text.TrimEnd() : text, style, from: new SourceSpan(slice.Start, slice.End));
            }
        }

        private void Paragraph(ContainerInline? inlines, RenderedStyle style)
        {
            if (inlines is null)
            {
                Add(string.Empty, style);
                return;
            }

            foreach (var inline in inlines)
                Inline(inline, style);

            // Ended in the paragraph's own style rather than in whatever the last run left behind. A
            // line ending draws nothing, so a link-coloured one is invisible — but it's still carried
            // out with the text when a selection spanning it is copied somewhere that keeps formatting.
            Add(string.Empty, style);
        }

        private void Inline(Inline inline, RenderedStyle style)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Add(literal.Content.ToString(), style, endsLine: false, from: literal.Span);
                    break;

                case EmphasisInline emphasis:
                    var amended = emphasis.DelimiterChar switch
                    {
                        '~' => style with { Strike = true },
                        _ => emphasis.DelimiterCount >= 2 ? style with { Bold = true } : style with { Italic = true },
                    };
                    foreach (var child in emphasis)
                        Inline(child, amended);
                    break;

                case CodeInline code:
                    // The span the backticks off it: what is drawn is the content, so an offset into
                    // the drawing has to map to the content and not to the marker in front of it.
                    Add(
                        code.Content,
                        style with { Fixed = true, Muted = true },
                        endsLine: false,
                        from: Inside(code.Span, code.DelimiterCount));
                    break;

                case LinkInline link:
                    // The words, as a link. Not the URL: a description pasted from a web page is mostly
                    // link text, and printing every target would drown it. Where it points goes with
                    // each run of the words instead, for the click and the hover to find.
                    //
                    // Only when it is somewhere that would actually be followed. A file: or javascript:
                    // link drawn as a link looks like something to click and then isn't, which is a
                    // worse answer than reading as the plain text it is.
                    var was = _link;
                    _link = Links.External(link.Url);

                    foreach (var child in link)
                        Inline(child, style);

                    _link = was;
                    break;

                // The angle-bracket form, which is a leaf rather than a link and so used to render as
                // nothing at all — the whole URL gone from the preview with no sign it was there.
                case AutolinkInline auto:
                    var outer = _link;
                    _link = auto.IsEmail ? null : Links.External(auto.Url);

                    // Likewise the angle brackets, which are written but not drawn.
                    Add(auto.Url, style, endsLine: false, from: Inside(auto.Span, 1));

                    _link = outer;
                    break;

                // Also a leaf. "&amp;" is written this way by anything that generates markdown from
                // HTML, and it was disappearing mid-sentence.
                case HtmlEntityInline entity:
                    Add(entity.Transcoded.ToString(), style, endsLine: false, from: entity.Span);
                    break;

                case TaskList task:
                    Add(task.Checked ? "[x] " : "[ ] ", style with { Muted = true }, endsLine: false, from: task.Span);
                    break;

                // Both kinds end the line now, since a bare newline is read as a break like any other.
                // Tight, because the air belongs under a paragraph rather than under every line of one:
                // spaced like a paragraph, one Return would look the same as two, and the difference
                // between a new line and a new thought is the reason for having both.
                case LineBreakInline:
                    Add(string.Empty, style with { Tight = true });
                    break;

                case ContainerInline container:
                    foreach (var child in container)
                        Inline(child, style);
                    break;
            }
        }

        /// <summary>Adds a run.</summary>
        /// <param name="text">The words</param>
        /// <param name="style">How to draw them</param>
        /// <param name="endsLine">Whether the run ends its line</param>
        /// <param name="from">Where in the markdown the run was written, or null for a run that isn't in it</param>
        private void Add(string text, RenderedStyle style, bool endsLine = true, SourceSpan? from = null)
            => Runs.Add(new RenderedRun(
                text,
                style,
                endsLine,
                from is { Length: > 0 } span ? (span.Start, span.Length) : null,
                _link));

        /// <summary>
        /// A span with its delimiters taken off each end.
        /// </summary>
        /// <remarks>
        /// For the runs that are drawn without the syntax that produced them — code between
        /// backticks, a URL between angle brackets. The whole span would map the first character of
        /// what is on screen to the marker in front of it, so every offset into the run would come
        /// out one short.
        /// </remarks>
        /// <param name="span">Where the whole thing was written, markers and all</param>
        /// <param name="delimiter">How many characters of marker sit at each end</param>
        /// <returns>Where its content was written</returns>
        private static SourceSpan Inside(SourceSpan span, int delimiter)
            => span.Length > delimiter * 2
                ? new SourceSpan(span.Start + delimiter, span.End - delimiter)
                : span;
    }
}
