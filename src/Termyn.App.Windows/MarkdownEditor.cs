using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// A task's description as it is written, with the markdown drawn as what it means.
/// </summary>
/// <remarks>
/// The text is the markdown the account stores, markers and all. Nothing is hidden and nothing is
/// rewritten — the styling is painted over the source between keystrokes, so what is saved is
/// exactly what is on screen and a description can't be quietly turned into a poorer version of
/// itself by being looked at.
///
/// The control's own undo queue is switched off, because a rich edit control records applying a
/// colour or a font as an undoable action: left on, Ctrl+Z answers by un-highlighting rather than
/// by undoing. <see cref="DescriptionHistory"/> stands in for it.
/// </remarks>
internal sealed class MarkdownEditor : RichTextBox
{
    /// <summary>How much bigger than the body each heading level is, as a fraction of it.</summary>
    private static readonly float[] HeadingScale = [0.5f, 0.25f, 0.1f, 0.1f, 0.05f, 0.05f];

    private Theme _theme = Theme.Resolve(Core.Settings.ThemePreference.System);

    /// <summary>The last text that was styled, so an unchanged one isn't styled again.</summary>
    private string? _styled;

    /// <summary>True while this control is doing the changing, so it doesn't answer itself.</summary>
    private bool _styling;

    public MarkdownEditor()
    {
        BorderStyle = BorderStyle.None;
        ScrollBars = RichTextBoxScrollBars.Vertical;
        AcceptsTab = false;

        // The links are drawn from the markdown, not guessed at by the control — which would
        // otherwise underline half an address mid-typing and fight with the styling.
        DetectUrls = false;
    }

    /// <summary>What to show when there is nothing in the box.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>The colours to draw with.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            BackColor = value.Panel;
            ForeColor = value.Text;

            // The colours all changed, so what was styled was styled in the old ones.
            _styled = null;
            Restyle();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Nought disables the queue outright. Nothing else can: the formatting this control applies
        // between keystrokes goes onto that queue whatever we do, and suspending it through the
        // Text Object Model was tried and does not take.
        SendMessage(Handle, EmSetUndoLimit, 0, 0);

        _styled = null;
        Restyle();
    }

    /// <summary>
    /// Replaces what is in the box, leaving the caret and the scroll where they were.
    /// </summary>
    /// <remarks>
    /// Assigning <see cref="Control.Text"/> collapses the caret to the start of the box, and
    /// <see cref="Restyle"/> can't put it back afterwards: it saves the selection when it runs, and
    /// by then the selection is already nought. So the place is taken before the assignment and
    /// handed back after it.
    ///
    /// The place was measured against text that has just been replaced, so it can point past the
    /// end of the new one — clamped rather than trusted.
    /// </remarks>
    /// <param name="text">What the box should hold</param>
    internal void Refill(string text)
    {
        // Nothing to keep a place in yet, and asking for the scroll position would realise the
        // control as a side effect of being told what to show.
        if (!IsHandleCreated)
        {
            Text = text;
            return;
        }

        var selection = SelectionStart;
        var length = SelectionLength;
        var scroll = ScrollPosition();

        Text = text;
        Restyle();

        var start = Math.Clamp(selection, 0, TextLength);
        Select(start, Math.Clamp(length, 0, TextLength - start));
        ScrollTo(scroll);
    }

    /// <summary>
    /// Draws the markdown as what it means, leaving every character of it where it is.
    /// </summary>
    /// <remarks>
    /// Called on a pause in the typing rather than on each keystroke: styling means selecting each
    /// run in turn, and doing that between two letters is work nobody is waiting on, done in the
    /// place they are working. Internal so a test can force it on a control that was never shown —
    /// styling a run needs a window behind it.
    /// </remarks>
    internal void Restyle()
    {
        if (!IsHandleCreated || _styling)
            return;

        var text = Text;
        if (text == _styled)
            return;

        _styling = true;

        // Drawing off for the duration: each run repaints on its own otherwise, and the whole
        // thing flickers in the box the user is typing into.
        SendMessage(Handle, WmSetRedraw, 0, 0);

        var selection = SelectionStart;
        var length = SelectionLength;
        var scroll = ScrollPosition();

        try
        {
            // The whole document at once, rather than a selection and two property sets per run.
            // Run by run was measured at 1,583 ms for a full-length description — three thousand
            // runs, each of them a round trip that reflows the control. This is one.
            //
            // It also settles what used to need a separate pass: the document is built from
            // nothing each time, so a word that was bold until its asterisks were deleted comes
            // back plain without anything having to notice that it changed.
            Rtf = BuildRtf(text);
            _styled = text;
        }
        finally
        {
            Select(selection, length);
            ScrollTo(scroll);

            SendMessage(Handle, WmSetRedraw, 1, 0);
            Invalidate();

            _styling = false;
        }
    }

    /// <summary>
    /// The whole description as a rich text document, styled.
    /// </summary>
    /// <remarks>
    /// Written out rather than applied run by run because a rich edit control takes a document in
    /// one message and a selection's formatting in three per run — and a full-length description
    /// is three thousand runs. The colours and the two faces are declared once at the top and each
    /// run then names them, which is what keeps this proportional to the text rather than to the
    /// number of things in it.
    /// </remarks>
    private string BuildRtf(string text)
    {
        var body = (int)Math.Round(Font.SizeInPoints * 2);   // RTF counts in half-points

        var rtf = new StringBuilder(text.Length * 2 + 256);
        rtf.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil ")
           .Append(Font.FontFamily.Name)
           .Append(@";}{\f1\fmodern ")
           .Append(Faces.FixedWidth.Name)
           .Append(@";}}");

        rtf.Append(@"{\colortbl ;")
           .Append(Colour(_theme.Text))
           .Append(Colour(_theme.Muted))
           .Append(Colour(_theme.Accent))
           .Append('}');

        foreach (var run in MarkdownHighlight.Runs(text))
        {
            var heading = run.Style is >= MarkdownStyle.Heading1 and <= MarkdownStyle.Heading6;
            var level = heading ? run.Style - MarkdownStyle.Heading1 : 0;
            var size = heading ? (int)Math.Round(body * (1f + HeadingScale[level])) : body;

            rtf.Append(run.Style == MarkdownStyle.Code ? @"\f1" : @"\f0")
               .Append(@"\fs").Append(size)
               .Append(heading || run.Style == MarkdownStyle.Strong ? @"\b" : @"\b0")
               .Append(run.Style is MarkdownStyle.Emphasis or MarkdownStyle.Quote ? @"\i" : @"\i0")
               .Append(run.Style == MarkdownStyle.Struck ? @"\strike" : @"\strike0")
               .Append(@"\cf").Append(ColourIndex(run.Style))
               .Append(' ');

            Escape(rtf, text.AsSpan(run.Start, run.Length));
        }

        // The last \par of a document ends the paragraph it is on rather than opening an empty one
        // after it, so a description ending in a newline came back a newline shorter — and pressing
        // Return at the end of one, which is where it is nearly always pressed, undid itself as soon
        // as the styling caught up. One more \par gives that final empty line somewhere to be.
        if (text.EndsWith('\n'))
            rtf.Append(@"\par ");

        return rtf.Append('}').ToString();
    }

    /// <summary>Which entry of the colour table a style is drawn in.</summary>
    private static int ColourIndex(MarkdownStyle style) => style switch
    {
        MarkdownStyle.LinkText => 3,
        MarkdownStyle.Marker or MarkdownStyle.Url or MarkdownStyle.Code
            or MarkdownStyle.Rule or MarkdownStyle.Quote => 2,
        _ => 1,
    };

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
    /// </remarks>
    private static void Escape(StringBuilder rtf, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\' or '{' or '}':
                    rtf.Append('\\').Append(c);
                    break;

                case '\n':
                    rtf.Append(@"\par ");
                    break;

                // The control holds a line ending as one newline, so this is only reachable from
                // text that arrived with one on its own. Dropped rather than drawn as a blank.
                case '\r':
                    break;

                case '\t':
                    rtf.Append(@"\tab ");
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

    /// <summary>
    /// Swallows the change this control made to itself.
    /// </summary>
    /// <remarks>
    /// Styling replaces the document, which the control reports as the text having changed. It
    /// hasn't — not by a character — and letting that out would restart the wait for the typing to
    /// stop on every restyle, which is a wait that ends in another restyle.
    /// </remarks>
    protected override void OnTextChanged(EventArgs e)
    {
        if (_styling)
            return;

        // Anything else that changed the text took the styling with it — assigning Text replaces
        // the document with a plain one. So what was last styled is no longer what is on screen,
        // however alike the two read: two tasks whose descriptions match to the character would
        // otherwise leave the second one drawn flat.
        _styled = null;

        base.OnTextChanged(e);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);

        _styled = null;
        Restyle();
    }

    /// <summary>
    /// Draws the hint over an empty box.
    /// </summary>
    /// <remarks>
    /// By hand, because a rich edit control has no placeholder of its own and paints itself. Drawn
    /// after the control has, so it sits on top of the background it just laid down.
    /// </remarks>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmPaint || TextLength > 0 || Placeholder.Length == 0 || Focused)
            return;

        using var graphics = Graphics.FromHwnd(Handle);
        using var brush = new SolidBrush(_theme.Muted);

        // Where the caret sits, so the hint reads as text that would be replaced rather than as a
        // label pasted into the corner.
        graphics.DrawString(Placeholder, Font, brush, 1, 1);
    }

    /// <summary>
    /// Redraws an empty box when the focus arrives or leaves, since the hint is shown to one of
    /// those and not the other and the control has no reason of its own to repaint for it.
    /// </summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);

        if (TextLength == 0 && Placeholder.Length > 0)
            Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);

        if (TextLength == 0 && Placeholder.Length > 0)
            Invalidate();
    }

    /// <summary>Where the box is scrolled to, so styling it can put it back.</summary>
    private Point ScrollPosition()
    {
        var point = new Point();
        SendMessage(Handle, EmGetScrollPos, 0, ref point);
        return point;
    }

    private void ScrollTo(Point position) => SendMessage(Handle, EmSetScrollPos, 0, ref position);

    private const int WmSetRedraw = 0x000B;
    private const int WmPaint = 0x000F;
    private const int EmSetUndoLimit = 0x0400 + 82;
    private const int EmGetScrollPos = 0x0400 + 221;
    private const int EmSetScrollPos = 0x0400 + 222;

    // DllImport rather than LibraryImport, matching the rest of the app: the generated marshalling
    // for the latter wants unsafe code for a struct passed by reference.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, int wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, int wParam, ref Point point);
}
