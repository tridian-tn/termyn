using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Termyn.App.Windows;

/// <summary>
/// A search box with something to click to empty it.
/// </summary>
/// <remarks>
/// The cross is a label sitting on the box rather than something painted onto it. The box is a
/// native control and drawing over one is how the task list came to flicker: a draw that arrives
/// outside a paint goes straight to the screen. A child control is painted by the same machinery
/// as everything else and needs none of that.
///
/// It shows only when there is something to clear, and it sits inside the box's border rather than
/// against it — filling the box's height put it across the line at the top and the bottom.
///
/// Quiet at rest and lit under the pointer, which is the bargain a search box makes: the cross is
/// not what anyone is looking at until they want it.
/// </remarks>
internal sealed class SearchBox : TextBox
{
    /// <summary>EM_SETMARGINS, and the flag for the right-hand one.</summary>
    private const int EmSetMargins = 0x00D3;

    private const int EcRightMargin = 0x0002;

    /// <summary>How wide the cross is, how far it sits in from the edges, and the two together.</summary>
    private const int ResetWidth = 18;

    private const int Inset = 3;

    private const int ResetRoom = ResetWidth + (Inset * 2);

    /// <summary>
    /// The icon fonts Windows draws its own clear buttons with, newest first.
    /// </summary>
    /// <remarks>
    /// Asked for by name and checked, because a font that isn't installed is fallen back on
    /// silently and the glyph would come out as an empty box. Where neither is on the machine a
    /// plain multiplication sign stands in, which every font has.
    /// </remarks>
    private static readonly string[] IconFonts = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    /// <summary>The clear glyph in those fonts, and what to show without them.</summary>
    private const string IconGlyph = "";

    private const string PlainGlyph = "✕";

    private readonly Label _reset;

    private Theme _theme = Theme.Resolve(Core.Settings.ThemePreference.System);

    private bool _lit;

    public SearchBox()
    {
        _reset = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            Visible = false,

            // A cross says nothing read aloud. This is the only thing here that can be clicked,
            // and it is the one thing a reader would have no other way of finding.
            AccessibleName = "Clear search",
            AccessibleRole = AccessibleRole.PushButton,
        };

        Dress(_reset);

        _reset.Click += (_, _) => Reset();
        _reset.MouseEnter += (_, _) => Highlight(true);
        _reset.MouseLeave += (_, _) => Highlight(false);

        // Put back rather than set once. Applying a theme walks every control it can reach and
        // paints a label the window's background, which is not the box's — so the cross would end
        // up sitting on a strip of the wrong colour, and most visibly in the dark theme.
        _reset.BackColorChanged += (_, _) => Colour();

        Controls.Add(_reset);
    }

    /// <summary>The colours to draw with, given the way every other control here is given them.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            Colour();
        }
    }

    /// <summary>Whether the cross is on show, which is whenever there is something to clear.</summary>
    public bool ShowingReset => _reset.Visible;

    /// <summary>What the cross is sitting on. Internal so a test can hold it to what it should be.</summary>
    internal Color ResetBackColour => _reset.BackColor;

    /// <summary>What the cross is drawn in.</summary>
    internal Color ResetColour => _reset.ForeColor;

    /// <summary>What the cross is drawn as, which is one of the two this knows about.</summary>
    internal string ResetGlyph => _reset.Text;

    /// <summary>The glyph Windows uses, and the stand-in, for a test to hold the choice to.</summary>
    internal static (string Native, string Plain) Glyphs => (IconGlyph, PlainGlyph);

    /// <summary>
    /// Lights the cross, or puts it back.
    /// </summary>
    /// <remarks>
    /// Called as the pointer arrives and leaves. Internal so a test can say so without a pointer,
    /// since calling this is the whole of what the pointer does.
    /// </remarks>
    /// <param name="over">Whether the pointer is on it</param>
    internal void Highlight(bool over)
    {
        _lit = over;
        Colour();
    }

    /// <summary>
    /// Empties the box, as the cross does.
    /// </summary>
    /// <remarks>
    /// The focus goes to the box rather than staying on a cross that has just disappeared, since
    /// what someone does after clearing a search is usually type another one.
    /// </remarks>
    public void Reset()
    {
        Clear();

        if (CanFocus)
            Focus();
    }

    /// <summary>
    /// Whether Escape is this box's to take, which it is only when there is something to clear.
    /// </summary>
    /// <remarks>
    /// An Escape in an empty box is left alone rather than quietly eaten. Nothing else in the
    /// window wants it while the search box has the focus, but a key swallowed by whatever happens
    /// to be focused is the sort of thing nobody can account for later.
    /// </remarks>
    internal bool TakesEscape => TextLength > 0;

    /// <summary>
    /// Empties the box on Escape, the way Escape leaves anything else here that is part-way
    /// through — the description goes back to reading, a comment stops being edited.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        => TakeEscape(keyData) || base.ProcessCmdKey(ref message, keyData);

    /// <summary>
    /// Empties the box if that is what the keystroke asks for.
    /// </summary>
    /// <remarks>
    /// Split out from the override for the same reason the outline's fold is: a test can say which
    /// keystroke it means, where PreProcessMessage would mix in whatever modifier the real keyboard
    /// happens to be holding and ask about a different key entirely.
    /// </remarks>
    /// <param name="keyData">The keystroke, modifiers and all</param>
    /// <returns>Whether it was this box's to answer</returns>
    internal bool TakeEscape(Keys keyData)
    {
        if (keyData != Keys.Escape || !TakesEscape)
            return false;

        Reset();
        return true;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);

        // Nothing to be lit about once it has gone, so the next time it appears it appears at rest.
        if (TextLength == 0)
            _lit = false;

        _reset.Visible = TextLength > 0;
        Colour();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        Colour();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        Colour();
        PlaceReset();

        // Keeps the words off the cross. Without it a long search runs under it and the last of
        // what was typed is behind the thing offering to delete it.
        SendMessage(Handle, EmSetMargins, EcRightMargin, ResetRoom << 16);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        PlaceReset();
    }

    /// <summary>Gives the cross its glyph, and the font that glyph lives in.</summary>
    private static void Dress(Label reset)
    {
        var installed = IconFonts.FirstOrDefault(Installed);

        reset.Text = installed is null ? PlainGlyph : IconGlyph;

        if (installed is not null)
            reset.Font = new Font(installed, 9f);
    }

    /// <summary>Whether a font family of that name is on this machine.</summary>
    private static bool Installed(string family)
    {
        try
        {
            using var found = new FontFamily(family);
            return true;
        }
        catch (ArgumentException)
        {
            // What a name nothing answers to gives back, which is the question being asked.
            return false;
        }
    }

    /// <summary>
    /// Colours the cross for where the pointer is.
    /// </summary>
    /// <remarks>
    /// Quiet on the box's own colour at rest, and on the row colour under the pointer — the same
    /// shade a hovered row in either list is drawn with, so this borrows a light the window already
    /// uses rather than inventing one.
    /// </remarks>
    private void Colour()
    {
        var wanted = _lit ? _theme.Row : BackColor;

        if (_reset.BackColor != wanted)
            _reset.BackColor = wanted;

        _reset.ForeColor = _lit ? _theme.Text : _theme.Muted;
    }

    /// <summary>
    /// Puts the cross inside the border rather than against it, and rounds it into a pill.
    /// </summary>
    /// <remarks>
    /// The inset is the whole of the first complaint about this one: filling the box's height put
    /// the cross across the line at the top and the bottom of it.
    /// </remarks>
    private void PlaceReset()
    {
        var room = Math.Max(ClientSize.Height - (Inset * 2), 1);
        var size = Math.Min(ResetWidth, room);

        _reset.Bounds = new Rectangle(
            ClientSize.Width - size - Inset,
            (ClientSize.Height - size) / 2,
            size,
            size);

        var previous = _reset.Region;

        using var pill = new GraphicsPath();
        pill.AddEllipse(0, 0, size, size);
        _reset.Region = new Region(pill);

        previous?.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, int wParam, nint lParam);
}
