using System.ComponentModel;
using Termyn.Core.Capture;
using Termyn.Presentation;

using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// The one way a day is asked for: a box to type it into, a calendar beside it to pick it off, and
/// a line under it saying what it was read as.
/// </summary>
/// <remarks>
/// <para>
/// A due date and a deadline both ask with this, so the same act looks and behaves the same
/// whichever date it is. What each will take is the dialog's to say, through the verdict it's built
/// with; how the words are read is the parser's, and is the same grammar quick add reads with.
/// </para>
/// <para>
/// The box and the calendar stay in step. Picking a day writes it into the box, in words the
/// grammar reads back, and the calendar opens on whatever day the box reads as. The box is the
/// answer: there's no second copy of the day to drift from it.
/// </para>
/// </remarks>
internal sealed class DayBox : UserControl
{
    /// <summary>How big the calendar on the button is drawn, before the screen's scaling.</summary>
    private const int GlyphSize = 16;

    private readonly Func<string, DayReading> _read;
    private readonly Func<DayReading, DayVerdict> _judge;
    private readonly DateOnly _today;
    private readonly bool _takesTime;
    private readonly bool _english;

    private readonly HintTextBox _text;
    private readonly Button _pick;
    private readonly Label _says;
    private readonly MonthCalendar _calendar;
    private readonly ToolStripControlHost _host;
    private readonly ToolStripDropDown _drop;
    private readonly ToolTip _tips = new();

    private Bitmap? _glyph;

    /// <summary>
    /// Whether the calendar is being moved from here rather than by a person, when the day it
    /// lands on isn't a choice to write into the box.
    /// </summary>
    private bool _steering;

    /// <summary>Builds the box.</summary>
    /// <param name="read">Reads what's typed, as the grammar does everywhere else</param>
    /// <param name="judge">Says what the line under the box reads, and whether the dialog may take it</param>
    /// <param name="today">The day the calendar opens on when the box doesn't name one</param>
    /// <param name="takesTime">Whether a time typed with a day belongs to the answer, and stays when another day is picked</param>
    /// <param name="english">Whether the account reads a typed date in English, which decides whether a day picked is written in words or figures</param>
    internal DayBox(Func<string, DayReading> read, Func<DayReading, DayVerdict> judge, DateOnly today, bool takesTime, bool english)
    {
        _read = read;
        _judge = judge;
        _today = today;
        _takesTime = takesTime;
        _english = english;

        Size = new Size(392, 56);

        _text = new HintTextBox
        {
            Hint = DayBoxText.Hint(english),
            Location = new Point(0, 0),
            Size = new Size(354, 27),
            AccessibleName = "Day",
        };

        _pick = new Button
        {
            Location = new Point(360, 0),
            Size = new Size(32, 27),
            AccessibleName = "Pick a day",
        };

        // The button says nothing in words, so there has to be somewhere to find out what it does.
        _tips.SetToolTip(_pick, "Pick a day");

        _says = new Label
        {
            Location = new Point(0, 34),
            Size = new Size(392, 20),
            AutoEllipsis = true,
            UseMnemonic = false,
            ForeColor = SystemColors.GrayText,
        };

        _calendar = new MonthCalendar { MaxSelectionCount = 1 };

        // The drop is built before anything is wired to it, so the handlers below aren't closing
        // over a field that isn't there yet.
        _host = new ToolStripControlHost(_calendar) { Margin = Padding.Empty, Padding = Padding.Empty };
        _drop = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true };
        _drop.Items.Add(_host);

        _pick.Click += (_, _) => ShowCalendar();
        _calendar.DateSelected += (_, e) => Chose(DateOnly.FromDateTime(e.Start));

        // Arrow keys move the selection without picking anything, so the box follows them too: the
        // calendar is closed with Enter or Escape, and what it was left on is the answer.
        _calendar.DateChanged += (_, e) => Chose(DateOnly.FromDateTime(e.Start), keepOpen: true);
        _calendar.KeyDown += (_, e) => CalendarKey(e);
        _text.TextChanged += (_, _) => Reread();

        DrawGlyph();
        Controls.AddRange([_text, _pick, _says]);
        Reread();
    }

    /// <summary>Raised when what the box reads as has changed.</summary>
    internal event EventHandler? ReadingChanged;

    /// <summary>What the box's text reads as.</summary>
    internal DayReading Reading { get; private set; } = DayReading.Blank;

    /// <summary>What the line under the box says, and whether the dialog may take it.</summary>
    internal DayVerdict Verdict { get; private set; } = new(string.Empty, true);

    /// <summary>What's in the box.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Typed
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    /// <summary>What the line under the box says.</summary>
    internal string Says => _says.Text;

    /// <summary>The box the day is typed into, for the dialog to put the focus in.</summary>
    internal TextBox Entry => _text;

    /// <summary>
    /// Writes a day into the box, as picking it off the calendar does.
    /// </summary>
    /// <remarks>
    /// A time already in the box stays with the new day: picking a different Tuesday isn't asking
    /// to lose the nine o'clock. A box that doesn't take a time drops it, since it was never part of
    /// an answer that box could give.
    /// </remarks>
    /// <param name="day">The day picked</param>
    internal void Pick(DateOnly day)
    {
        var time = _takesTime && Reading.Kind is DayReadingKind.Day ? Reading.Time : null;
        _text.Text = QuickAddParser.Written(day, time, _english);
    }

    /// <summary>
    /// Puts a time on the day the box reads as, or takes it off.
    /// </summary>
    /// <remarks>
    /// Rewrites the box rather than holding the time somewhere beside it, so what's typed stays
    /// the whole answer. Does nothing while the box isn't a day: a repeat carries its time in its
    /// own words, and there's no day to put one on otherwise.
    /// </remarks>
    /// <param name="time">The time, or null for the whole day</param>
    internal void SetTime(TimeOnly? time)
    {
        if (Reading is { Kind: DayReadingKind.Day, Day: { } day })
            _text.Text = QuickAddParser.Written(day, time, _english);
    }

    /// <summary>Opens the calendar, as pressing the button does.</summary>
    internal void PressPick() => InvokeOnClick(_pick, EventArgs.Empty);

    /// <summary>Whether the calendar is up.</summary>
    internal bool CalendarOpen => _drop.Visible;

    /// <summary>Whether the calendar has the keys, which is what makes it usable without a mouse.</summary>
    internal bool CalendarFocused => _calendar.Focused;

    /// <summary>The day the calendar is on.</summary>
    internal DateOnly CalendarDay => DateOnly.FromDateTime(_calendar.SelectionStart);

    /// <summary>Whether what the box built has been let go of.</summary>
    internal bool Released => _drop.IsDisposed;

    /// <summary>What hovering the calendar button says.</summary>
    internal string PickTip => _tips.GetToolTip(_pick) ?? string.Empty;

    /// <summary>How big the calendar on the button came out, which follows the screen's scaling.</summary>
    internal Size GlyphDrawn => _pick.Image?.Size ?? Size.Empty;

    /// <summary>Takes a day off the calendar, as choosing one in it does.</summary>
    /// <param name="day">The day chosen</param>
    internal void PickFromCalendar(DateOnly day) => Chose(day);

    /// <summary>Presses a key in the calendar, for a test with no calendar to type into.</summary>
    /// <param name="key">The key pressed</param>
    /// <returns>The key as the handler left it, which says whether it was taken</returns>
    internal KeyEventArgs PressInCalendar(Keys key)
    {
        var pressed = new KeyEventArgs(key);
        CalendarKey(pressed);
        return pressed;
    }

    /// <summary>Reads the box again, and says what it found.</summary>
    private void Reread()
    {
        Reading = _read(_text.Text);
        Verdict = _judge(Reading);
        _says.Text = Verdict.Says;
        ReadingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes a day chosen in the calendar into the box.
    /// </summary>
    /// <param name="day">The day now chosen</param>
    /// <param name="keepOpen">Whether the calendar stays up, which it does while keys move it</param>
    private void Chose(DateOnly day, bool keepOpen = false)
    {
        if (_steering)
            return;

        Pick(day);

        if (!keepOpen)
            _drop.Close();
    }

    /// <summary>
    /// Closes the calendar on Enter or Escape, leaving it on whatever day it was showing.
    /// </summary>
    /// <remarks>
    /// The key is swallowed rather than let go: the dialog answers Escape with Cancel and Enter
    /// with OK, so a key that carried on past here would dismiss the calendar and the dialog behind
    /// it in one press — throwing away the day the user had just settled on.
    /// </remarks>
    /// <param name="pressed">The key pressed in the calendar</param>
    private void CalendarKey(KeyEventArgs pressed)
    {
        if (pressed.KeyCode is not (Keys.Enter or Keys.Escape))
            return;

        _drop.Close();
        pressed.Handled = true;
        pressed.SuppressKeyPress = true;
    }

    /// <summary>
    /// Drops the calendar under the button that asks for it, on the day the box reads as.
    /// </summary>
    /// <remarks>
    /// Moving the calendar there isn't a choice of that day, so it isn't written back into the box:
    /// a box reading "tomorrow" would otherwise be rewritten as a date just for looking.
    ///
    /// The focus is put on the host rather than the calendar: a control inside a drop-down isn't
    /// on the form's own focus chain, and asking the control itself does nothing — which would
    /// leave the calendar open and deaf to every key.
    /// </remarks>
    private void ShowCalendar()
    {
        _steering = true;
        try
        {
            _calendar.SetDate(Clamp(Reading.Day ?? _today).ToDateTime(TimeOnly.MinValue));
        }
        finally
        {
            _steering = false;
        }

        _drop.Show(_pick, new Point(0, _pick.Height));
        _host.Focus();
    }

    /// <summary>
    /// The day, held to what the calendar is able to show.
    /// </summary>
    /// <remarks>
    /// A date is whatever the account's JSON said, and one before 1753 — an import, or another
    /// client's bug — makes the control throw on being given it. The box still holds the real day;
    /// only the calendar opens on the nearest it can show.
    /// </remarks>
    /// <param name="day">The day asked for</param>
    /// <returns>That day, or the nearest one the calendar can show</returns>
    private DateOnly Clamp(DateOnly day)
    {
        var least = DateOnly.FromDateTime(_calendar.MinDate);
        var most = DateOnly.FromDateTime(_calendar.MaxDate);

        return day < least ? least : day > most ? most : day;
    }

    /// <summary>
    /// Draws the button's calendar at the size this screen wants it.
    /// </summary>
    /// <remarks>
    /// Redrawn rather than scaled: the glyph is a handful of rectangles, and stretching a sixteen
    /// pixel bitmap to a two-hundred-per-cent screen gives a blurred one. Called again when the
    /// window lands on a monitor with different scaling.
    /// </remarks>
    private void DrawGlyph()
    {
        var size = LogicalToDeviceUnits(GlyphSize);
        var drawn = new Bitmap(size, size);

        using (var into = Graphics.FromImage(drawn))
            CalendarGlyph.Draw(into, new Rectangle(0, 0, size, size), SystemColors.ControlText);

        _pick.Image = drawn;

        // Assigned before the old one goes, so nothing is ever asked to draw an image that's been
        // let go of.
        _glyph?.Dispose();
        _glyph = drawn;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        DrawGlyph();
    }

    protected override void Dispose(bool disposing)
    {
        // Here rather than on a close: neither of these is a child control, so the base disposal
        // doesn't reach them, and a box built and dropped without ever being shown — which is every
        // test of it — would never raise anything to hang them off.
        if (disposing)
        {
            _drop.Dispose();
            _tips.Dispose();
            _glyph?.Dispose();
        }

        base.Dispose(disposing);
    }
}
