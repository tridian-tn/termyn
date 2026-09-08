using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// What has been done to the account this session, newest first.
/// </summary>
/// <remarks>
/// Reading only. Nothing here undoes anything — Ctrl+Z already does that, and an entry is a note of
/// what happened rather than a handle on it. Half of these are past taking back anyway: the engine
/// keeps a bounded number of undoable writes and this list outlives them.
///
/// A time and a sentence, and nothing else. The list has to be skimmable to be worth opening, and a
/// column of ids or resource types would make it a log rather than an account of an afternoon.
/// </remarks>
internal sealed class HistoryForm : Form
{
    private readonly ListBox _list;
    private readonly Label _empty;
    private readonly Func<IReadOnlyList<HistoryEntry>> _read;
    private readonly Action _cleared;

    private HistoryForm(Theme theme, Func<IReadOnlyList<HistoryEntry>> read, Action cleared)
    {
        _read = read;
        _cleared = cleared;

        Text = "What you've done";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ClientSize = new Size(520, 380);
        MinimumSize = new Size(360, 240);
        BackColor = theme.Panel;
        ForeColor = theme.Text;

        var heading = new Label
        {
            // Says the two things somebody would otherwise have to find out by being surprised:
            // this session only, and it is not the account's own activity log.
            Text = "Changes made in Termyn since it started.",
            Location = new Point(14, 12),
            Size = new Size(492, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = theme.Muted,
        };

        _list = new ListBox
        {
            Location = new Point(14, 38),
            Size = new Size(492, 288),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            IntegralHeight = false,
            BackColor = theme.Panel,
            ForeColor = theme.Text,
        };

        // Sits where the list is, for when the list has nothing to show. A blank box says the
        // feature is broken; this says there is nothing to say yet.
        _empty = new Label
        {
            Text = "Nothing yet.",
            Location = _list.Location,
            Size = _list.Size,
            Anchor = _list.Anchor,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = theme.Muted,
            Visible = false,
        };

        var clear = new Button
        {
            Text = "Clear",
            Location = new Point(14, 338),
            Size = new Size(90, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        clear.Click += (_, _) =>
        {
            _cleared();
            Fill();
        };

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Location = new Point(416, 338),
            Size = new Size(90, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        Controls.AddRange([heading, _empty, _list, clear, close]);
        CancelButton = close;

        theme.Apply(this);
        Fill();
    }

    /// <summary>
    /// Reads the list in.
    /// </summary>
    /// <remarks>
    /// The time in the account's own short form and then the sentence. Seconds are left off: this
    /// is for finding your way back to something you did, not for reconciling a log.
    /// </remarks>
    private void Fill()
    {
        var entries = _read();

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var entry in entries)
                _list.Items.Add($"{entry.At.ToLocalTime():HH:mm}   {entry.Said}");
        }
        finally
        {
            _list.EndUpdate();
        }

        _list.Visible = entries.Count > 0;
        _empty.Visible = entries.Count == 0;
    }

    /// <summary>
    /// Shows what has been done, and lets it be forgotten.
    /// </summary>
    /// <param name="owner">The window to sit over</param>
    /// <param name="theme">The colours to draw with</param>
    /// <param name="read">The list as it now stands, asked for again after it is cleared</param>
    /// <param name="cleared">Called when the user would rather it didn't say</param>
    public static void Show(
        IWin32Window owner,
        Theme theme,
        Func<IReadOnlyList<HistoryEntry>> read,
        Action cleared)
    {
        using var dialog = new HistoryForm(theme, read, cleared);
        dialog.ShowDialog(owner);
    }
}
