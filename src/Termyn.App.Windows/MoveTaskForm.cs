using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// Picks a project or a section to move a task to: type a few letters of either, or of the path to
/// it, and confirm.
/// </summary>
/// <remarks>
/// A box over a list rather than a combo box and its own autocomplete. The framework's only matches
/// from the start of an entry, so a section could only be found by typing out the project it's in
/// first — and a picker for nested places is mostly used to find the one you can't already see.
///
/// Owner-drawn for the same reason the palette is: an account's projects and sections can run to
/// hundreds, and a control apiece would make opening it cost more than using it.
/// </remarks>
internal sealed class MoveTaskForm : Form
{
    private readonly IReadOnlyList<MoveDestination> _all;
    private readonly Theme _theme;
    private readonly TextBox _query;
    private readonly ListBox _results;
    private readonly Button _move;

    private IReadOnlyList<MoveDestination> _shown = [];

    /// <summary>Whether the list is a ranking of what's been typed rather than the whole tree.</summary>
    private bool _searching;

    /// <summary>Internal rather than private so a test can lay one out without showing it.</summary>
    /// <param name="task">What the task is called, for the line above the box</param>
    /// <param name="destinations">Everywhere it could go, in the sidebar's order</param>
    /// <param name="theme">The colours to draw the list in</param>
    internal MoveTaskForm(string task, IReadOnlyList<MoveDestination> destinations, Theme theme)
    {
        _all = destinations;
        _theme = theme;

        Text = "Move to";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 400);

        var heading = new Label
        {
            Text = task,
            Location = new Point(14, 12),
            Size = new Size(392, 20),
            AutoEllipsis = true,

            // The account's text rather than a caption of ours, so an ampersand in it is a character.
            UseMnemonic = false,
        };

        _move = new Button { Text = "Move", DialogResult = DialogResult.OK, Location = new Point(226, 356), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(318, 356), Size = new Size(88, 30) };

        _query = new TextBox
        {
            Location = new Point(14, 38),
            Size = new Size(392, 27),
            PlaceholderText = "Project or section…",
        };
        _query.TextChanged += (_, _) => ShowMatches();

        _results = new ListBox
        {
            Location = new Point(14, 72),
            Size = new Size(392, 270),
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Math.Max(22, Font.Height + 8),
        };
        _results.DrawItem += OnDrawItem;
        _results.SelectedIndexChanged += (_, _) => _move.Enabled = CanMove;
        _results.DoubleClick += (_, _) =>
        {
            if (CanMove)
                DialogResult = DialogResult.OK;
        };

        // Enter goes to Move, and does nothing while Move is greyed — which is what stops a bare
        // Enter on the place the task already sits from sending it there anyway.
        AcceptButton = _move;
        CancelButton = cancel;
        Controls.AddRange([heading, _query, _results, _move, cancel]);

        // The caret starts in the box, which is where the picker is used from.
        ActiveControl = _query;

        theme.Apply(this);

        // After the theme, which gives every label the body text colour. This one names what's being
        // moved rather than being anything to read as an instruction.
        heading.ForeColor = theme.Muted;

        ShowMatches();
    }

    /// <summary>The row the list is on, or null when it's on none.</summary>
    internal MoveDestination? Selected
        => _results.SelectedIndex >= 0 && _results.SelectedIndex < _shown.Count ? _shown[_results.SelectedIndex] : null;

    /// <summary>Whether confirming now would move the task anywhere.</summary>
    internal bool CanMove => Selected is { Here: false };

    /// <summary>Asks where a task should go.</summary>
    /// <param name="owner">The window to centre on</param>
    /// <param name="task">What the task is called</param>
    /// <param name="destinations">Everywhere it could go, in the sidebar's order</param>
    /// <param name="theme">The colours to draw the list in</param>
    /// <returns>Where to, or null when the dialog was cancelled</returns>
    public static MoveDestination? Pick(
        IWin32Window owner,
        string task,
        IReadOnlyList<MoveDestination> destinations,
        Theme theme)
    {
        using var dialog = new MoveTaskForm(task, destinations, theme);
        return dialog.ShowDialog(owner) == DialogResult.OK && dialog.CanMove ? dialog.Selected : null;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Arrows move through the list while the caret stays in the box, so the query can be
        // narrowed without reaching back up to it. The list answers to its own once it has the focus.
        if (_query.Focused && keyData is Keys.Down or Keys.Up)
        {
            Step(keyData == Keys.Down ? 1 : -1);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Named apart from the control's own Move event, which is a different thing entirely.</summary>
    private void Step(int offset)
    {
        if (_results.Items.Count == 0)
            return;

        _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + offset, 0, _results.Items.Count - 1);
    }

    /// <summary>Fills the list with whatever matches the box. Not Refresh, which is the control's own repaint.</summary>
    private void ShowMatches()
    {
        _searching = _query.Text.Trim().Length > 0;
        _shown = MoveDestinations.Rank(_all, _query.Text);

        _results.BeginUpdate();
        try
        {
            _results.Items.Clear();
            foreach (var destination in _shown)
                _results.Items.Add(destination.Path);

            // Browsing, the list opens on where the task already is — which says where that is, and
            // leaves Move greyed until something else is picked. A sub-task is here nowhere, so
            // nothing is picked for it until asked.
            //
            // Searching, the best match is the one to take, passing over where the task already is.
            // Its own section is the likeliest thing to match when the name is shared, and landing
            // there left Enter doing nothing with a match sitting right under it. Only when that's
            // all there is does the list rest on it.
            var shown = _shown.ToList();
            var here = shown.FindIndex(d => d.Here);

            _results.SelectedIndex = !_searching ? here
                : shown.FindIndex(d => !d.Here) is var best and >= 0 ? best
                : here;
        }
        finally
        {
            _results.EndUpdate();
        }

        _move.Enabled = CanMove;
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count)
            return;

        var destination = _shown[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;

        using (var background = new SolidBrush(selected ? _theme.Accent : _theme.Panel))
            e.Graphics.FillRectangle(background, e.Bounds);

        var text = selected ? _theme.OnAccent : _theme.Text;
        var muted = selected ? _theme.OnAccent : _theme.Muted;

        // A child project and a section look alike once they're indented under the same parent, so
        // each row says which it is.
        var kind = destination.Kind == SidebarKind.Section ? "section" : "project";
        var kindWidth = TextRenderer.MeasureText(kind, Font).Width + 12;

        var mark = e.Bounds with { X = e.Bounds.X + 6, Width = Gutter };
        if (destination.Here)
            TextRenderer.DrawText(e.Graphics, Tick, Font, mark, text, Flags);

        // Browsing, the list is the tree, and the indent says what's inside what. Searching, it's a
        // ranking and the rows no longer sit under their parents, so each says its whole path.
        var indent = _searching ? 0 : destination.Depth * Font.Height;
        var label = e.Bounds with
        {
            X = e.Bounds.X + 6 + Gutter + indent,
            Width = Math.Max(0, e.Bounds.Width - kindWidth - Gutter - 12 - indent),
        };
        TextRenderer.DrawText(e.Graphics, _searching ? destination.Path : destination.Name, Font, label, text, Flags);

        var hint = e.Bounds with { X = e.Bounds.Right - kindWidth, Width = kindWidth - 6 };
        TextRenderer.DrawText(e.Graphics, kind, Font, hint, muted, Flags | TextFormatFlags.Right);
    }

    /// <summary>The mark the place a task already sits carries, so drawing it and measuring it can't drift.</summary>
    private const string Tick = "✓";

    /// <summary>How much room the tick gets, whether or not this row has one.</summary>
    private int Gutter => TextRenderer.MeasureText(Tick, Font).Width + 4;

    private static TextFormatFlags Flags
        => TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
}
