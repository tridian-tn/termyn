using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Termyn.Core;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Termyn.App.Windows;

/// <summary>
/// A description as it reads rather than as it is written — the rendered half of the panel.
/// </summary>
/// <remarks>
/// Read-only, and deliberately: the text the account holds is what gets saved, so nothing here can
/// turn someone's description into a poorer version of itself. A rich-text editor that serialised
/// back to markdown would quietly drop whatever it didn't model, and descriptions arrive pasted
/// from all sorts of places.
/// </remarks>
internal sealed class MarkdownView : RichTextBox
{
    /// <summary>How far one level of nesting indents a list, in pixels.</summary>
    private const int IndentWidth = 16;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        // What Todoist's own editor can produce: bold, italic, strikethrough, headings, quotes,
        // code, lists and links. The extras cover the strikethrough, which plain markdown has no
        // syntax for, and bare URLs, which people paste far more often than they write links.
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        // Markdown proper reads a single newline as a space and wants a blank line or two trailing
        // spaces before it will break a line. Todoist doesn't: press Return once there and the line
        // breaks, which is how the descriptions in an account are already written. Following the
        // spec here would run those lines together and be right about nothing anybody typed.
        .UseSoftlineBreakAsHardlineBreak()
        // So a click on the rendering knows which character of the markdown it landed on. Without
        // it the spans are good enough to find a block by and not to put a caret with.
        .UsePreciseSourceLocation()
        .Build();

    /// <summary>How much air goes under a paragraph, in twips — a fifth of a line or so.</summary>
    private const int ParagraphSpacing = 120;

    private string _markdown = string.Empty;
    private Theme _theme = Theme.Resolve(Core.Settings.ThemePreference.System);
    private string _placeholder = string.Empty;
    private bool _inert;

    /// <summary>
    /// Where each link sits in the rendered text and where it points, built as the text is written.
    /// </summary>
    /// <remarks>
    /// The rendering shows a link's words and not its address, so by the time it is on screen there
    /// is nothing left in the text to open. This is what a click looks the address up in.
    /// </remarks>
    private readonly List<(int Start, int End, string Url)> _links = [];

    /// <summary>
    /// Where each run of the rendered text came from in the markdown behind it.
    /// </summary>
    /// <remarks>
    /// The rendering drops the markers and reorders nothing, so a rendered offset has a markdown
    /// offset under it — but only the writing knows which, since by the time it is on screen the
    /// syntax it was written with is gone. This is what a click looks that up in, so opening the
    /// text to type into it lands the caret where the user was pointing rather than at the top.
    /// </remarks>
    private readonly List<(int Start, int Length, int Source, int SourceLength)> _sources = [];

    /// <summary>Raised when the user asks to type into the description, with where in the markdown.</summary>
    public event Action<int>? EditRequested;

    /// <summary>Shows where a link goes before it is followed.</summary>
    private readonly ToolTip _tip = new();

    /// <summary>What the tip currently says, so it isn't reset on every pixel of movement.</summary>
    private string? _shownTip;

    /// <summary>The link the left button went down on, so a drag can't end by following one.</summary>
    private string? _pressedOn;

    /// <summary>Raised when a link in the description is clicked, with the address it points at.</summary>
    public event Action<string>? LinkOpened;

    public MarkdownView()
    {
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        DetectUrls = false;      // the links are drawn from the markdown, not guessed at afterwards
        ScrollBars = RichTextBoxScrollBars.Vertical;
    }

    /// <summary>The markdown to show. Setting it redraws.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Markdown
    {
        get => _markdown;
        set
        {
            var text = value ?? string.Empty;
            if (_markdown == text)
                return;

            _markdown = text;
            Rebuild();
        }
    }

    /// <summary>
    /// What to say over an empty pane — why there is nothing here, when that isn't obvious.
    /// </summary>
    /// <remarks>
    /// Drawn by hand for the same reason the editor's is: a rich edit control has no placeholder of
    /// its own and paints itself.
    /// </remarks>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            if (_placeholder == value)
                return;

            _placeholder = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>
    /// Whether anything here could be typed into, which decides how the pane is drawn.
    /// </summary>
    /// <remarks>
    /// Appearance only — the pane is kept out of use by being read-only rather than by being
    /// disabled, and this is what says so on screen. Recessed onto the background colour, which is
    /// the shade sitting behind the panel everywhere else in the window, so an inactive surface
    /// reads as one without needing a new colour invented for it.
    /// </remarks>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Inert
    {
        get => _inert;
        set
        {
            if (_inert == value)
                return;

            _inert = value;
            ApplyColours();

            // The words are drawn from the theme as they are written, so which colour they are is
            // settled at build time and changing it means building again.
            Rebuild();
            Invalidate();
        }
    }

    /// <summary>The colours to draw with.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            ApplyColours();
            Rebuild();
        }
    }

    private void ApplyColours()
    {
        BackColor = _inert ? _theme.Background : _theme.Panel;
        ForeColor = _theme.Text;
    }

    /// <summary>
    /// Draws whatever was set while there was nothing to draw on. The panel starts collapsed, so
    /// the first description often arrives before this control has a window of its own — and the
    /// setter would then have nothing to do the next time, having already stored that text.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Rebuild();
    }

    /// <summary>Draws the markdown into the box, from scratch each time.</summary>
    /// <remarks>
    /// Internal so a test can force the render on a control that was never shown: styling a run
    /// means selecting it, and a selection needs a window handle behind it.
    /// </remarks>
    internal void Rebuild()
    {
        if (!IsHandleCreated)
            return;

        // Drawing is switched off for the duration. Every run sets a selection and pushes text at
        // the control, and each of those repaints on its own — which is nearly all of the time a
        // long description takes to render, and the render happens where the user is typing.
        SendMessage(Handle, WmSetRedraw, 0, 0);
        try
        {
            Clear();
            _links.Clear();
            _sources.Clear();

            foreach (var block in Parse())
                WriteBlock(block, indent: 0);

            // Back to the top, so switching task doesn't leave the box scrolled to where the last
            // one happened to end.
            SelectionStart = 0;
            SelectionLength = 0;
            ScrollToCaret();
        }
        finally
        {
            SendMessage(Handle, WmSetRedraw, 1, 0);
            Invalidate();
        }
    }

    /// <summary>
    /// The markdown as blocks, or the whole thing as one plain run when it can't be read.
    /// </summary>
    /// <remarks>
    /// Markdig refuses input nested past its own limit — a hundred and twenty-eight quote markers,
    /// or sixty-four list levels — by throwing. A description is account data: it arrives by sync
    /// from the web app or another device, so this is not only reachable by typing, and letting it
    /// out would take down the window on the next publish with the offending task selected. Worse,
    /// the box that would let the user fix the text is the one that throws. The raw text is a
    /// truthful thing to show and always readable.
    /// </remarks>
    private IEnumerable<Block> Parse()
    {
        try
        {
            return Markdig.Markdown.Parse(_markdown, Pipeline);
        }
        catch (ArgumentException)
        {
            Write(_markdown, Style.Plain);
            return [];
        }
    }

    private void WriteBlock(Block block, int indent)
    {
        switch (block)
        {
            case HeadingBlock heading:
                // One size for H1 and a smaller one for everything below it: Todoist's editor only
                // offers two levels, and pasted markdown can go deeper than it is worth drawing.
                var larger = heading.Level <= 1 ? 0.5f : heading.Level == 2 ? 0.25f : 0.1f;
                WriteParagraph(heading.Inline, new Style(Bold: true, Larger: larger, Indent: indent));
                break;

            case ParagraphBlock paragraph:
                WriteParagraph(paragraph.Inline, new Style(Indent: indent));
                break;

            case QuoteBlock quote:
                foreach (var child in quote)
                    WriteBlock(child, indent + 1);
                break;

            case ListBlock list:
                WriteList(list, indent);
                break;

            case CodeBlock code:
                WriteCode(code, indent);
                break;

            // Not a container, so the fallback below never sees it, and its words vanished
            // entirely. A pasted snippet is worth showing as what it is rather than not at all.
            case HtmlBlock html:
                WriteLines(html, new Style(Fixed: true, Muted: true, Indent: indent + 1));
                break;

            case ThematicBreakBlock rule:
                Write("————————", new Style(Muted: true, Indent: indent), from: rule.Span);
                break;

            case ContainerBlock container:
                foreach (var child in container)
                    WriteBlock(child, indent);
                break;
        }
    }

    private void WriteList(ListBlock list, int indent)
    {
        var number = list.OrderedStart is { Length: > 0 } start && int.TryParse(start, out var first) ? first : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var bullet = list.IsOrdered ? $"{number++}. " : "•  ";

            // Every run of the item carries the same paragraph settings, because that is what they
            // are — set per paragraph, not per run, so the last word of a line would otherwise
            // decide the indent for the whole of it.
            var line = new Style(Indent: indent, Hanging: true, Tight: true);

            var lead = true;
            foreach (var child in item)
            {
                // The marker leads the item's first line and the rest follows hanging under it,
                // which is what makes a wrapped item read as one thing.
                if (lead && child is ParagraphBlock paragraph)
                {
                    Write(bullet, line with { Muted = true }, newLine: false);
                    WriteParagraph(paragraph.Inline, line);
                    lead = false;
                    continue;
                }

                WriteBlock(child, indent + 1);
                lead = false;
            }
        }
    }

    private void WriteCode(CodeBlock code, int indent)
        => WriteLines(code, new Style(Fixed: true, Muted: true, Indent: indent + 1));

    /// <summary>Writes a block that carries its text as raw lines rather than as inlines.</summary>
    private void WriteLines(LeafBlock block, Style style)
    {
        var text = new StringBuilder();
        foreach (var line in block.Lines.Lines)
        {
            if (line.Slice.Text is null)
                continue;

            text.AppendLine(line.Slice.ToString());
        }

        Write(text.ToString().TrimEnd(), style, from: block.Span);
    }

    private void WriteParagraph(ContainerInline? inlines, Style style)
    {
        if (inlines is null)
        {
            Write(string.Empty, style);
            return;
        }

        foreach (var inline in inlines)
            WriteInline(inline, style);

        // Ended in the paragraph's own style rather than in whatever the last run left behind. A
        // newline draws nothing, so a link-coloured one is invisible here — but it is still carried
        // out with the text when a selection spanning it is copied somewhere that keeps formatting.
        Write(string.Empty, style);
    }

    private void WriteInline(Inline inline, Style style)
    {
        switch (inline)
        {
            case LiteralInline literal:
                Write(literal.Content.ToString(), style, newLine: false, from: literal.Span);
                break;

            case EmphasisInline emphasis:
                var amended = emphasis.DelimiterChar switch
                {
                    '~' => style with { Strike = true },
                    _ => emphasis.DelimiterCount >= 2 ? style with { Bold = true } : style with { Italic = true },
                };
                foreach (var child in emphasis)
                    WriteInline(child, amended);
                break;

            case CodeInline code:
                // The span the backticks off it: what is drawn is the content, so an offset into
                // the drawing has to map to the content and not to the marker in front of it.
                Write(
                    code.Content,
                    style with { Fixed = true, Muted = true },
                    newLine: false,
                    from: Inside(code.Span, code.DelimiterCount));
                break;

            case LinkInline link:
                // The words, coloured as a link. Not the URL: a description pasted from a web page
                // is mostly link text, and printing every target would drown it. Where it points is
                // noted against the span instead, for the click and the hover to find.
                //
                // Coloured only when it is somewhere we would actually go. A file: or javascript:
                // link drawn in the link colour looks like something to click and then isn't, which
                // is a worse answer than reading as the plain text it is.
                var target = Links.External(link.Url);
                var from = TextLength;

                foreach (var child in link)
                    WriteInline(child, target is null ? style : style with { Link = true });

                if (target is not null && TextLength > from)
                    _links.Add((from, TextLength, target));
                break;

            // The angle-bracket form, which is a leaf rather than a link and so used to render as
            // nothing at all — the whole URL gone from the preview with no sign it was there.
            case AutolinkInline auto:
                var bare = auto.IsEmail ? null : Links.External(auto.Url);
                var opened = TextLength;

                // Likewise the angle brackets, which are written but not drawn.
                Write(
                    auto.Url,
                    bare is null ? style : style with { Link = true },
                    newLine: false,
                    from: Inside(auto.Span, 1));

                if (bare is not null && TextLength > opened)
                    _links.Add((opened, TextLength, bare));
                break;

            // Also a leaf. "&amp;" is written this way by anything that generates markdown from
            // HTML, and it was disappearing mid-sentence.
            case HtmlEntityInline entity:
                Write(entity.Transcoded.ToString(), style, newLine: false, from: entity.Span);
                break;

            case TaskList task:
                Write(task.Checked ? "[x] " : "[ ] ", style with { Muted = true }, newLine: false, from: task.Span);
                break;

            // Both kinds end the line now, since a bare newline is read as a break like any other.
            // Tight, because the air belongs under a paragraph rather than under every line of one:
            // spaced like a paragraph, one Return would look the same as two, and the difference
            // between a new line and a new thought is the reason for having both.
            case LineBreakInline:
                Write(string.Empty, style with { Tight = true });
                break;

            case ContainerInline container:
                foreach (var child in container)
                    WriteInline(child, style);
                break;
        }
    }

    /// <summary>Appends a run in the given style.</summary>
    /// <param name="text">The words to append</param>
    /// <param name="style">How to draw them</param>
    /// <param name="newLine">Whether the run ends its line</param>
    /// <param name="from">
    /// Where in the markdown this run was written, or null for a run that isn't in it — a bullet, a
    /// rule, the blank run that closes a paragraph
    /// </param>
    private void Write(string text, Style style, bool newLine = true, SourceSpan? from = null)
    {
        // The source length is the span's own, not the run's: a run of code is shorter than the
        // backticks it was written with, and a rule is longer than the three dashes that made it.
        // Clamping an offset into the run against the span keeps it inside what it came from.
        if (from is { Length: > 0 } span && text.Length > 0)
            _sources.Add((TextLength, text.Length, span.Start, span.Length));

        SelectionStart = TextLength;
        SelectionLength = 0;

        // Paragraph settings, so every run of a paragraph has to agree about them. The air under
        // each one is what makes a description read as separate thoughts rather than as a wall —
        // which is how it looks in Todoist, and the thing most obviously missing without it.
        SelectionIndent = style.Indent * IndentWidth;
        SelectionHangingIndent = style.Hanging ? IndentWidth : 0;
        SetSpacingAfter(style.Tight ? 0 : ParagraphSpacing);

        var font = FontStyle.Regular;
        if (style.Bold) font |= FontStyle.Bold;
        if (style.Italic) font |= FontStyle.Italic;
        if (style.Strike) font |= FontStyle.Strikeout;

        var family = style.Fixed ? Faces.FixedWidth : Font.FontFamily;
        SelectionFont = new Font(family, Font.Size * (1f + style.Larger), font);
        // Muted throughout when the pane can't be typed into, which is the only cue that carries in
        // both themes: the recessed background is a couple of units in the light one and invisible.
        // Links keep their colour — a completed task's description is still worth following out of, and
        // drawing them dead while they still work is the mirror of the mistake being avoided here.
        SelectionColor = style.Link ? Theme.Accent
            : _inert || style.Muted ? Theme.Muted
            : Theme.Text;

        AppendText(newLine ? text + Environment.NewLine : text);
    }

    /// <summary>
    /// A span with its delimiters taken off each end.
    /// </summary>
    /// <remarks>
    /// For the runs that are drawn without the syntax that produced them — code between backticks,
    /// a URL between angle brackets. The whole span would map the first character of what is on
    /// screen to the marker in front of it, so every offset into the run would come out one short.
    /// </remarks>
    /// <param name="span">Where the whole thing was written, markers and all</param>
    /// <param name="delimiter">How many characters of marker sit at each end</param>
    /// <returns>Where its content was written</returns>
    private static SourceSpan Inside(SourceSpan span, int delimiter)
        => span.Length > delimiter * 2
            ? new SourceSpan(span.Start + delimiter, span.End - delimiter)
            : span;

    /// <summary>The link at a point in the rendered text, or null where there is none.</summary>
    /// <remarks>
    /// Internal so a test can ask where the links landed without a mouse to point with.
    /// </remarks>
    internal string? LinkAt(int index)
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
    /// Internal so a test can ask without a mouse to point with.
    /// </remarks>
    /// <param name="index">An offset into the rendered text</param>
    /// <returns>The offset into the markdown, or zero when there is nothing to map</returns>
    internal int SourceAt(int index)
    {
        var after = -1;

        foreach (var (start, length, source, sourceLength) in _sources)
        {
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

    /// <summary>The link under a point on screen, or null.</summary>
    private string? LinkUnder(Point position)
    {
        var index = GetCharIndexFromPosition(position);
        if (index < 0)
            return null;

        // GetCharIndexFromPosition answers with the nearest character rather than saying there
        // isn't one, so a click past the end of a line would otherwise open whatever finished it.
        var box = GetPositionFromCharIndex(index);
        return Math.Abs(box.Y - position.Y) > Font.Height ? null : LinkAt(index);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var url = LinkUnder(e.Location);

        // The pointer says what is clickable, since nothing else about a link does — the words are
        // coloured, but so is anything else the theme decides to colour.
        Cursor = url is null ? Cursors.Default : Cursors.Hand;

        // And the tip says where it goes. The rendering shows a link's words rather than its
        // address, so nothing on screen otherwise contradicts words that claim to be one address
        // while pointing at another — which is a thing a description shared through an account can do.
        if (url != _shownTip)
        {
            _shownTip = url;
            _tip.SetToolTip(this, url ?? string.Empty);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressedOn = e.Button == MouseButtons.Left ? LinkUnder(e.Location) : null;
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        // The same link the press landed on. The box is read-only but still selectable, so dragging
        // a passage out of the description ends on a mouse-up that would otherwise open whatever it
        // finished over — copying a quotation would launch a browser.
        if (e.Button == MouseButtons.Left && _pressedOn is { } url && LinkUnder(e.Location) == url)
            LinkOpened?.Invoke(url);

        _pressedOn = null;
        base.OnMouseUp(e);
    }

    /// <summary>
    /// Draws the line explaining an empty pane, over the top of what the control has just painted.
    /// </summary>
    /// <remarks>
    /// Only when there is nothing else here: a completed task's description is shown and can't be
    /// edited, and there is no room to say so over the top of them. The recessed background is what
    /// carries it in that case.
    /// </remarks>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmPaint || TextLength > 0 || _placeholder.Length == 0)
            return;

        using var graphics = Graphics.FromHwnd(Handle);
        using var brush = new SolidBrush(_theme.Muted);

        graphics.DrawString(_placeholder, Font, brush, 1, 1);
    }

    /// <summary>
    /// Asks to type into the description, at the character that was double-clicked.
    /// </summary>
    /// <remarks>
    /// Not on a single click. The box is selectable, so one click is how a passage gets picked out
    /// to copy, and it is also how a link is followed — neither of which is a request to start
    /// editing. A link is left out of this entirely: the first of the two clicks already opened it,
    /// and moving the caret as well would make one gesture do two things.
    /// </remarks>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && LinkUnder(e.Location) is null)
            EditRequested?.Invoke(SourceAt(GetCharIndexFromPosition(e.Location)));

        base.OnMouseDoubleClick(e);
    }

    /// <summary>
    /// Asks to type into the description, at wherever the caret is sitting.
    /// </summary>
    /// <remarks>
    /// Enter and F2, which are what the outline already answers to for editing the thing under the
    /// selection. Suppressed rather than merely handled: the box is read-only, so both keys would
    /// otherwise reach it and be answered with the system beep.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.F2)
        {
            EditRequested?.Invoke(SourceAt(SelectionStart));
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Sets the air under the selected paragraph, in twips.
    /// </summary>
    /// <remarks>
    /// By hand, because WinForms exposes the indent side of a paragraph's format and not the
    /// spacing side. Only the one field is masked in, so everything the managed properties have
    /// already set on this paragraph is left where it is.
    /// </remarks>
    private void SetSpacingAfter(int twips)
    {
        const int EmSetParaFormat = 0x0400 + 71;
        const int ScfSelection = 0x0001;
        const int PfmSpaceAfter = 0x00000080;

        var format = new ParaFormat2
        {
            cbSize = Marshal.SizeOf<ParaFormat2>(),
            dwMask = PfmSpaceAfter,
            dySpaceAfter = twips,
        };

        SendMessage(Handle, EmSetParaFormat, ScfSelection, ref format);
    }

    // DllImport rather than LibraryImport, matching the platform layer: the generated marshalling
    // for the latter wants unsafe code for a struct passed by reference.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, int flags, ref ParaFormat2 format);

    /// <summary>Turns drawing off and on around a rebuild.</summary>
    private const int WmSetRedraw = 0x000B;

    /// <summary>The paint the placeholder is drawn on top of.</summary>
    private const int WmPaint = 0x000F;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, int wParam, nint lParam);

    /// <summary>
    /// The rich edit control's paragraph format, laid out as the control expects to find it.
    /// </summary>
    /// <remarks>
    /// Every field has to be here whether or not it is set, because the size is what the control
    /// reads first to tell this structure from the shorter one that came before it.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct ParaFormat2
    {
        public int cbSize;
        public int dwMask;
        public short wNumbering;
        public short wEffects;
        public int dxStartIndent;
        public int dxRightIndent;
        public int dxOffset;
        public short wAlignment;
        public short cTabCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] rgxTabs;

        public int dySpaceBefore;
        public int dySpaceAfter;
        public int dyLineSpacing;
        public short sStyle;
        public byte bLineSpacingRule;
        public byte bOutlineLevel;
        public short wShadingWeight;
        public short wShadingStyle;
        public short wNumberingStart;
        public short wNumberingStyle;
        public short wNumberingTab;
        public short wBorderSpace;
        public short wBorderWidth;
        public short wBorders;
    }

    /// <summary>
    /// How a run is drawn.
    /// </summary>
    /// <param name="Larger">
    /// How much bigger than the body text, as a fraction of it — so nought is the body size. Held
    /// that way round deliberately: a struct can be made without its primary constructor ever
    /// running, and a scale that defaulted to zero would then ask for a font of no size at all.
    /// </param>
    private readonly record struct Style(
        bool Bold = false,
        bool Italic = false,
        bool Strike = false,
        bool Fixed = false,
        bool Muted = false,
        bool Link = false,
        float Larger = 0f,
        int Indent = 0,
        bool Hanging = false,
        bool Tight = false)
    {
        public static readonly Style Plain = default;
    }
}
