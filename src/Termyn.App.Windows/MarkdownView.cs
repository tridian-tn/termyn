using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Termyn.Presentation;

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

    /// <summary>How much air goes under a paragraph, in twips — a fifth of a line or so.</summary>
    private const int ParagraphSpacing = 120;

    private string _markdown = string.Empty;
    private Theme _theme = Theme.Resolve(Core.Settings.ThemePreference.System);
    private string _placeholder = string.Empty;
    private bool _inert;

    /// <summary>
    /// The description as it reads: its runs, where its links are, and the way back from each part
    /// of it to the markdown.
    /// </summary>
    /// <remarks>
    /// Kept from the last draw, since that's what's on screen for a click or the pointer to ask
    /// about — and replaced whole by the next, so nothing from the task before can linger.
    /// </remarks>
    private MarkdownRendering _rendering = MarkdownRendering.Empty;

    /// <summary>
    /// The document being built, handed to the box whole when it's done. Kept between renders and
    /// emptied at the start of each, since a description is redrawn on every sync.
    /// </summary>
    private readonly StringBuilder _rtf = new();

    /// <summary>The body text's size in points, read once a render rather than once a run.</summary>
    private float _body;

    /// <summary>
    /// The paragraph settings last written into the document, so a run that shares them doesn't
    /// write them again.
    /// </summary>
    private (int Indent, bool Hanging, bool Tight)? _paragraph;

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

    /// <summary>Raised when Ctrl and the wheel have scaled the box, with the scale it's now at.</summary>
    public event Action<float>? ZoomWheeled;

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
    /// Internal so a test can force the render on a control that was never shown: handing a control
    /// a document needs a window handle behind it.
    /// </remarks>
    internal void Rebuild()
    {
        if (!IsHandleCreated)
            return;

        // Loading a document puts the box back to its own size, and the user may have it scaled.
        // Switching task or a sync coming in is no reason for it to stop being.
        var zoom = ZoomLevel.Of(this);

        // Drawing is switched off for the duration, so the box isn't painted empty and then full in
        // the place the user is reading.
        SendMessage(Handle, WmSetRedraw, 0, 0);
        try
        {
            // The whole rendering in one message, rather than a selection set, styled and written
            // for every run — which for a full-length description was thousands of round trips to
            // the control, each of them reflowing it, where the user is reading.
            RichText.Load(this, Document());

            // Back to the top, so switching task doesn't leave the box scrolled to where the last
            // one happened to end.
            SelectionStart = 0;
            SelectionLength = 0;
            ScrollToCaret();
        }
        finally
        {
            // After the scroll, which doesn't mind: the top is the top at any scale.
            zoom.ApplyTo(this);

            SendMessage(Handle, WmSetRedraw, 1, 0);
            Invalidate();
        }
    }

    /// <summary>
    /// The description as one rich text document.
    /// </summary>
    /// <remarks>
    /// What each run says, how it's styled and where it came from is <see cref="MarkdownRendering"/>'s
    /// to work out, and it's kept for the clicks and the hover to ask. What's left here is writing the
    /// runs down the way the box reads them.
    /// </remarks>
    /// <returns>The document, ready to hand to the box</returns>
    private StringBuilder Document()
    {
        _rendering = MarkdownRendering.Of(_markdown);

        _paragraph = null;
        _body = Font.SizeInPoints;
        _rtf.Clear();
        RichText.Open(_rtf, Font.FontFamily.GetName(0), _theme);

        var endsLine = false;
        foreach (var run in _rendering.Runs)
        {
            WriteParagraph(run.Style);
            WriteCharacters(run.Style, link: run.Link is not null);

            RichText.Escape(_rtf, run.Text);
            if (run.EndsLine)
                _rtf.Append(@"\par ");

            if (run.EndsLine || run.Text.Length > 0)
                endsLine = run.EndsLine || run.Text[^1] is '\r' or '\n';
        }

        // The last paragraph mark of a document ends the paragraph it's on rather than opening an
        // empty one after it, so the box would otherwise hold one character fewer than the rendering
        // counts — and every offset is only right while the two agree.
        if (endsLine)
            _rtf.Append(@"\par ");

        return _rtf.Append('}');
    }

    /// <summary>
    /// Sets out the paragraph a run is in, when it differs from the one before.
    /// </summary>
    /// <remarks>
    /// Paragraph settings, so every run of a paragraph has to agree about them — and the document
    /// takes whichever were in force when the paragraph ends, which is the last run's. The air under
    /// each one is what makes a description read as separate thoughts rather than as a wall, which
    /// is how it looks in Todoist and the thing most obviously missing without it.
    ///
    /// The indent is where the first line starts and the hanging indent is how much further in the
    /// rest sit. A document writes the same thing the other way round: where the rest sit, and how
    /// far back from there the first line comes out.
    /// </remarks>
    private void WriteParagraph(RenderedStyle style)
    {
        var paragraph = (style.Indent, style.Hanging, style.Tight);
        if (_paragraph == paragraph)
            return;

        _paragraph = paragraph;

        var indent = Twips(style.Indent * IndentWidth);
        var hanging = style.Hanging ? Twips(IndentWidth) : 0;

        _rtf.Append(@"\pard\li").Append(indent + hanging)
            .Append(@"\fi").Append(-hanging)
            .Append(@"\sa").Append(style.Tight ? 0 : ParagraphSpacing);
    }

    /// <summary>Sets out how a run's characters are drawn, in full, ahead of its text.</summary>
    /// <param name="style">How the run is drawn</param>
    /// <param name="link">Whether the run is part of a link that would be followed</param>
    private void WriteCharacters(RenderedStyle style, bool link)
    {
        // Muted throughout when the pane can't be typed into, which is the only cue that carries in
        // both themes: the recessed background is a couple of units in the light one and invisible.
        // Links keep their colour — a completed task's description is still worth following out of, and
        // drawing them dead while they still work is the mirror of the mistake being avoided here.
        var colour = link ? RichText.AccentColour
            : _inert || style.Muted ? RichText.MutedColour
            : RichText.TextColour;

        // RTF counts a size in half-points.
        var size = (int)Math.Round(_body * 2 * (1f + style.Larger));

        _rtf.Append(@"\f").Append(style.Fixed ? RichText.FixedFace : RichText.BodyFace)
            .Append(@"\fs").Append(size)
            .Append(style.Bold ? @"\b" : @"\b0")
            .Append(style.Italic ? @"\i" : @"\i0")
            .Append(style.Strike ? @"\strike" : @"\strike0")
            .Append(@"\cf").Append(colour)
            .Append(' ');
    }

    /// <summary>
    /// Pixels on the screen as twips, the way the control's own indent properties convert them.
    /// </summary>
    /// <remarks>
    /// Against the screen's resolution rather than this control's, which is what those properties
    /// do — so an indent comes out where it did when it was set through them.
    /// </remarks>
    /// <param name="pixels">A distance on the screen</param>
    /// <returns>The same distance in twentieths of a point</returns>
    private static int Twips(int pixels) => (int)(pixels / (double)ScreenDpi * 72.0 * 20.0);

    /// <summary>How many pixels the screen fits into an inch, across.</summary>
    private static readonly float ScreenDpi = ReadScreenDpi();

    private static float ReadScreenDpi()
    {
        using var screen = Graphics.FromHwnd(IntPtr.Zero);
        return screen.DpiX;
    }

    /// <summary>
    /// Lets go of the tip this shows an address in.
    /// </summary>
    /// <remarks>
    /// The tip is a window of its own and belongs to nothing else — there's no components container
    /// here to dispose it — so it lasted until the finaliser.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>
    /// How much text the rendering says the box holds.
    /// </summary>
    /// <remarks>
    /// Internal so a test can hold it against what the box says it is holding. The two agreeing is
    /// what every link and every click's way back to the markdown depends on, and a description only
    /// has to reach here with a line ending the document writes differently from how the rendering
    /// counts it for the two to part company.
    /// </remarks>
    internal int Counted => _rendering.Length;

    /// <summary>The link at a point in the rendered text, or null where there is none.</summary>
    /// <remarks>Internal so a test can ask where the links landed without a mouse to point with.</remarks>
    /// <param name="index">An offset into the rendered text</param>
    /// <returns>Where the link there goes, or null</returns>
    internal string? LinkAt(int index) => _rendering.LinkAt(index);

    /// <summary>Where in the markdown the rendered text at this index was written.</summary>
    /// <remarks>Internal so a test can ask without a mouse to point with.</remarks>
    /// <param name="index">An offset into the rendered text</param>
    /// <returns>The offset into the markdown, or zero when there is nothing to map</returns>
    internal int SourceAt(int index) => _rendering.SourceAt(index);

    /// <summary>The link under a point on screen, or null.</summary>
    /// <remarks>Internal so a test can point at a scaled line without a mouse to point with.</remarks>
    /// <param name="position">The point, in the box's own coordinates</param>
    /// <returns>Where the link there goes, or null when there isn't one</returns>
    internal string? LinkUnder(Point position)
    {
        var index = GetCharIndexFromPosition(position);
        if (index < 0)
            return null;

        // GetCharIndexFromPosition answers with the nearest character rather than saying there
        // isn't one, so a click past the end of a line would otherwise open whatever finished it.
        // A line's as tall as the scale makes it, so the allowance is too: unscaled, the bottom of
        // every line in a zoomed panel stopped being part of its link.
        var box = GetPositionFromCharIndex(index);
        return Math.Abs(box.Y - position.Y) > Font.Height * ZoomFactor ? null : LinkAt(index);
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
    /// Draws the line explaining an empty pane, over the top of what the control has just painted,
    /// and says when the wheel has scaled the box.
    /// </summary>
    /// <remarks>
    /// Only when there is nothing else here: a completed task's description is shown and can't be
    /// edited, and there is no room to say so over the top of them. The recessed background is what
    /// carries it in that case. Drawn at the scale the text would be, since it stands in for it.
    ///
    /// The wheel scales the control behind its zoom property's back, so the property is read
    /// straight after — which is how it learns — and the window told, so the other half of the panel
    /// can follow.
    /// </remarks>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (ZoomLevel.IsZoomWheel(m))
        {
            var scale = ZoomFactor;
            ZoomWheeled?.Invoke(scale);
            return;
        }

        if (m.Msg != WmPaint || TextLength > 0 || _placeholder.Length == 0)
            return;

        using var graphics = Graphics.FromHwnd(Handle);
        using var brush = new SolidBrush(_theme.Muted);
        using var font = ZoomLevel.ScaledFont(this);

        graphics.DrawString(_placeholder, font, brush, 1, 1);
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

    /// <summary>Turns drawing off and on around a rebuild.</summary>
    private const int WmSetRedraw = 0x000B;

    /// <summary>The paint the placeholder is drawn on top of.</summary>
    private const int WmPaint = 0x000F;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, int wParam, nint lParam);
}
