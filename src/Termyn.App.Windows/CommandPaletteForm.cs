using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The Ctrl+K palette: type a few letters, pick a place to go or a thing to do.
/// </summary>
/// <remarks>
/// Owner-drawn list rather than one row per control: the palette holds every project, label and
/// filter in the account, and a control apiece would make opening it cost more than using it.
/// </remarks>
internal sealed class CommandPaletteForm : Form
{
    private readonly Func<string, IReadOnlyList<PaletteEntry>> _search;
    private readonly Theme _theme;
    private readonly TextBox _query;
    private readonly ListBox _results;

    private IReadOnlyList<PaletteEntry> _entries = [];

    /// <summary>Internal rather than private so a test can open one without a screen to show it on.</summary>
    internal CommandPaletteForm(Func<string, IReadOnlyList<PaletteEntry>> search, Theme theme)
    {
        _search = search;
        _theme = theme;

        Text = "Termyn — commands";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 360);
        KeyPreview = true;

        _query = new TextBox { Dock = DockStyle.Top, Height = 28, PlaceholderText = "Go to, or do…" };
        _query.TextChanged += (_, _) => Refresh(_query.Text);

        _results = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 22,
        };
        _results.DrawItem += OnDrawItem;
        _results.DoubleClick += (_, _) => Accept();

        Controls.Add(_results);
        Controls.Add(_query);

        // The caret starts in the box, which is the whole point of the palette: it opens to be
        // typed at. Without this the focus lands on the results list — first in the collection,
        // because the box is added after it so as to dock above it — and the first few letters
        // went to the list's own type-ahead instead of the query.
        ActiveControl = _query;

        theme.Apply(this);
        Refresh(string.Empty);
    }

    /// <summary>What the user chose, or null if they dismissed it.</summary>
    public PaletteEntry? Chosen { get; private set; }

    /// <summary>Opens the palette and returns the chosen entry, or null.</summary>
    public static PaletteEntry? Pick(IWin32Window owner, Func<string, IReadOnlyList<PaletteEntry>> search, Theme theme)
    {
        using var form = new CommandPaletteForm(search, theme);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Chosen : null;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                DialogResult = DialogResult.Cancel;
                Close();
                return true;

            case Keys.Enter:
                Accept();
                return true;

            // Arrows move through the results while the caret stays in the box, so the query can be
            // narrowed without reaching back up to it.
            case Keys.Down:
                Step(1);
                return true;

            case Keys.Up:
                Step(-1);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Named apart from the control's own Move event, which is a different thing entirely.</summary>
    private void Step(int offset)
    {
        if (_results.Items.Count == 0)
            return;

        var next = _results.SelectedIndex + offset;
        _results.SelectedIndex = Math.Clamp(next, 0, _results.Items.Count - 1);
    }

    private void Accept()
    {
        if (_results.SelectedIndex < 0 || _results.SelectedIndex >= _entries.Count)
            return;

        Chosen = _entries[_results.SelectedIndex];
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Refresh(string query)
    {
        _entries = _search(query);

        _results.BeginUpdate();
        try
        {
            _results.Items.Clear();
            foreach (var entry in _entries)
                _results.Items.Add(entry.Label);

            if (_entries.Count > 0)
                _results.SelectedIndex = 0;
        }
        finally
        {
            _results.EndUpdate();
        }
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _entries.Count)
            return;

        var entry = _entries[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;

        using (var background = new SolidBrush(selected ? _theme.Accent : _theme.Panel))
            e.Graphics.FillRectangle(background, e.Bounds);

        var text = selected ? _theme.OnAccent : _theme.Text;
        var muted = selected ? _theme.OnAccent : _theme.Muted;

        var kind = entry.Hint;
        var kindWidth = TextRenderer.MeasureText(kind, Font).Width + 12;

        // A tick for an entry that is already on, which is how the palette says what a menu says
        // with its own. In a gutter every row has rather than in front of the ones that need it,
        // so the names stay in a line whether or not there is anything to the left of them.
        var mark = e.Bounds with { X = e.Bounds.X + 6, Width = Gutter };
        if (entry.Checked)
            TextRenderer.DrawText(e.Graphics, Tick, Font, mark, text, Flags);

        var label = e.Bounds with
        {
            X = e.Bounds.X + 6 + Gutter,
            Width = Math.Max(0, e.Bounds.Width - kindWidth - Gutter - 12),
        };
        TextRenderer.DrawText(e.Graphics, entry.Label, Font, label, text, Flags);

        var hint = e.Bounds with { X = e.Bounds.Right - kindWidth, Width = kindWidth - 6 };
        TextRenderer.DrawText(e.Graphics, kind, Font, hint, muted, Flags | TextFormatFlags.Right);
    }

    /// <summary>The mark an entry already on carries, so drawing it and measuring it can't drift.</summary>
    private const string Tick = "✓";

    /// <summary>
    /// How much room the tick gets, whether or not this row has one.
    /// </summary>
    /// <remarks>
    /// Measured rather than fixed. The window follows the scaling of whatever monitor it is on, and
    /// the mark is drawn in a font that follows it too — so a width in pixels that looks right on
    /// one screen clips the mark or runs it into the name on the next.
    /// </remarks>
    private int Gutter => TextRenderer.MeasureText(Tick, Font).Width + 4;

    private static TextFormatFlags Flags
        => TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
}
