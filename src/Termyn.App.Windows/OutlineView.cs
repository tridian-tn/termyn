using System.ComponentModel;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The task outline: a virtual, owner-drawn list. Rows are drawn rather than composed from controls
/// so that indentation, the priority flag and the labels cost nothing per row, and only the visible
/// rows are ever realised.
/// </summary>
internal sealed class OutlineView : ListView
{
    private const int IndentWidth = 18;
    private const int TextInset = 6;

    private IReadOnlyList<TaskRow> _rows = [];

    /// <summary>Struck through, for a completed row. Built once rather than per cell painted.</summary>
    private Font? _struck;

    /// <summary>
    /// Virtual mode asks for the same row repeatedly — on every hover, focus change and repaint —
    /// and expects the same instance back each time. Handing out a fresh one makes the control
    /// re-evaluate item state and the selection follows the mouse.
    /// </summary>
    private ListViewItem?[] _cache = [];

    /// <summary>Cached so painting doesn't ask the native control once per cell.</summary>
    private int _selectedIndex = -1;

    /// <summary>
    /// True while the selection is being put back on the task it was already on, so the two native
    /// events that takes aren't published as the user having chosen something.
    /// </summary>
    private bool _reseating;

    /// <summary>The selection last published, so a refresh that lands back where it was is silent.</summary>
    private int _publishedIndex = -1;

    public OutlineView()
    {
        View = View.Details;
        VirtualMode = true;
        OwnerDraw = true;
        FullRowSelect = true;
        MultiSelect = false;
        HideSelection = false;
        HoverSelection = false;
        HotTracking = false;
        LabelEdit = true;
        HeaderStyle = ColumnHeaderStyle.Clickable;
        DoubleBuffered = true;

        // Each header carries the column it stands for, so a click has something to name without a
        // second table of indices to keep in step with this one.
        Columns.Add("Task", 360).Tag = TaskColumn.Content;
        Columns.Add("!", 46, HorizontalAlignment.Center).Tag = TaskColumn.Priority;
        Columns.Add("Project", 140).Tag = TaskColumn.Project;
        Columns.Add("Due", 120).Tag = TaskColumn.Due;
        Columns.Add("Labels", 140).Tag = TaskColumn.Labels;
    }

    /// <summary>Raised when a header is clicked, with the column it stands for.</summary>
    public event Action<TaskColumn>? SortRequested;

    private TaskSort _sort = TaskSort.Default;

    /// <summary>
    /// Which column the rows are ordered by, marked with an arrow on that header. Named apart from
    /// ListView.Sort, which sorts a list this one never lets the control own.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TaskSort Ordering
    {
        get => _sort;
        set
        {
            if (_sort == value)
                return;

            _sort = value;

            // Children as well: the header is a window of its own, and invalidating only the list
            // would leave the arrow on whichever column had it last.
            Invalidate(invalidateChildren: true);
        }
    }

    protected override void OnColumnClick(ColumnClickEventArgs e)
    {
        if (e.Column >= 0 && e.Column < Columns.Count && Columns[e.Column].Tag is TaskColumn column)
            SortRequested?.Invoke(column);

        base.OnColumnClick(e);
    }

    /// <summary>
    /// The colours to draw with. Not the system ones: on a dark theme the highlight and grey-text
    /// system colours are the light-theme values, so a selected row came out unreadable.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Theme Theme { get; set; } = Theme.Resolve(ThemePreference.System);

    protected override void OnFontChanged(EventArgs e)
    {
        _struck?.Dispose();
        _struck = null;
        base.OnFontChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _struck?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The strike-through font, made on first use so a theme change can't leak one.</summary>
    private Font Struck => _struck ??= new Font(Font, FontStyle.Strikeout);

    /// <summary>
    /// Asks what a task is called now, for a selected row that has stopped being found by the name
    /// it had. Null leaves the old behaviour, where any such row is treated as gone.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string, string>? Renamed { get; set; }

    /// <summary>The rows to show. Selection is preserved by id where the task is still present.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<TaskRow> Rows
    {
        get => _rows;
        set
        {
            var selected = SelectedId;

            _rows = value;
            _cache = new ListViewItem?[value.Count];
            VirtualListSize = value.Count;

            // Bookkeeping, not a choice the user made. Clearing and re-adding an index raises the
            // selection event twice, and the moment in between has nothing selected — which anything
            // listening reads as the user having stepped off the task. Held quiet and published once
            // at the end, when the selection is whatever it is going to be.
            _reseating = true;
            try
            {
                // Put the selection back on the same task, if it's still here. Deliberately no
                // scrolling and no fallback selection: a background sync refreshes these rows every
                // 45 seconds, and it must not move the selection or the viewport under the user.
                var index = selected is null ? -1 : IndexOf(selected);

                // Not here under the name it had, which for a task created a moment ago means the
                // sync has just been told what the server calls it. Same task, same row, new name —
                // and indistinguishable from a deletion without something that knows the difference.
                if (index < 0 && selected is not null && Renamed?.Invoke(selected) is { } now && now != selected)
                {
                    index = IndexOf(now);
                    selected = now;
                }
                if (index >= 0)
                {
                    if (SelectedIndices.Count != 1 || SelectedIndices[0] != index)
                    {
                        SelectedIndices.Clear();
                        SelectedIndices.Add(index);
                    }

                    // Keyboard navigation moves from the focused row, not the selected one, so they
                    // must not drift apart. Setting focus here does not scroll the viewport.
                    Items[index].Focused = true;
                }
                else
                {
                    // The selected task is gone. Leaving the native selection on its old index would
                    // silently hand the selection to whichever task now occupies that row.
                    SelectedIndices.Clear();
                }

                _selectedIndex = index;
            }
            finally
            {
                _reseating = false;
            }

            // Once, now that it has settled — and only when it has actually landed somewhere else,
            // so a refresh that changed nothing stays as quiet as it was before.
            if (_selectedIndex != _publishedIndex)
                base.OnSelectedIndexChanged(EventArgs.Empty);

            _publishedIndex = _selectedIndex;
            Invalidate();
        }
    }

    public string? SelectedId
        => SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count ? _rows[SelectedIndices[0]].Id : null;

    public TaskRow? SelectedRow
        => SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count ? _rows[SelectedIndices[0]] : null;

    /// <summary>Selects a task and scrolls it into view. For explicit navigation, not for refreshes.</summary>
    public void SelectId(string id)
    {
        if (IndexOf(id) is var index and >= 0)
        {
            SelectedIndices.Clear();
            SelectedIndices.Add(index);
            EnsureVisible(index);
        }
    }

    /// <summary>
    /// Scrolls the selected task back into view, for when the room it was in has been taken.
    /// </summary>
    /// <remarks>
    /// Asked for rather than done on its own. The rows are rebuilt every time the sync comes round,
    /// and a list that scrolled itself then would move the viewport under somebody reading it — so
    /// this is only for the moments the user has just changed how much room the list has.
    /// </remarks>
    public void ShowSelection()
    {
        if (SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count)
            EnsureVisible(SelectedIndices[0]);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        _selectedIndex = SelectedIndices.Count > 0 ? SelectedIndices[0] : -1;

        // Held while the rows are being reassigned; that path publishes once when it is done.
        if (_reseating)
            return;

        _publishedIndex = _selectedIndex;
        base.OnSelectedIndexChanged(e);
    }

    /// <summary>
    /// A right-click moves the selection to the row under the pointer, so the menu that follows is
    /// about the task being pointed at rather than whichever one was selected beforehand. Done on
    /// the press, because the menu is raised from the release and the selection has to have moved
    /// by then. A click past the last row leaves the selection alone — there is nothing to act on,
    /// and the menu declines to open.
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right
            && HitTest(e.Location).Item?.Index is { } index
            && index >= 0
            && index < _rows.Count)
        {
            if (SelectedIndices.Count != 1 || SelectedIndices[0] != index)
            {
                SelectedIndices.Clear();
                SelectedIndices.Add(index);
            }

            // Keyboard navigation carries on from the focused row, as it does everywhere else here.
            Items[index].Focused = true;
        }

        base.OnMouseDown(e);
    }

    private bool IsSelected(int index) => _selectedIndex == index;

    private int IndexOf(string id)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>
    /// Tab and Shift+Tab indent and outdent here rather than moving focus out of the list — but only
    /// with a row to act on, and never with Ctrl held, so there is always a way to tab out.
    /// </summary>
    protected override bool IsInputKey(Keys keyData)
        => ((keyData & Keys.KeyCode) == Keys.Tab
            && (keyData & Keys.Control) == 0
            && SelectedIndices.Count > 0)
           || base.IsInputKey(keyData);

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex >= _cache.Length)
        {
            // Virtual mode requires a sub-item per column, or the control throws from its own
            // window procedure — which a guard against a stale index must not do.
            e.Item = new ListViewItem(new string[Columns.Count]);
            return;
        }

        if (_cache[e.ItemIndex] is not { } cached)
        {
            var row = _rows[e.ItemIndex];
            cached = new ListViewItem([ContentOf(row), string.Empty, row.Project, DueOf(row), LabelsOf(row)]) { Tag = row.Id };
            _cache[e.ItemIndex] = cached;
        }

        e.Item = cached;
    }

    /// <summary>Room for the sort arrow at the end of a header.</summary>
    private const int ArrowWidth = 14;

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawBackground();

        var bounds = Inset(e.Bounds);

        if (!Ordering.IsDefault && e.Header?.Tag is TaskColumn column && column == Ordering.Column)
        {
            // Drawn as a glyph of its own at the end of the header rather than added to the text,
            // so the narrow priority column shows which way it is sorted instead of ellipsizing
            // the arrow away.
            var arrow = bounds with { X = bounds.Right - ArrowWidth, Width = ArrowWidth };
            TextRenderer.DrawText(
                e.Graphics,
                Ordering.Descending ? "▼" : "▲",
                Font,
                arrow,
                Theme.Accent,
                Flags | TextFormatFlags.Right);

            bounds = bounds with { Width = Math.Max(0, bounds.Width - ArrowWidth) };
        }

        var alignment = e.Header?.TextAlign switch
        {
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            HorizontalAlignment.Right => TextFormatFlags.Right,
            _ => TextFormatFlags.Left,
        };

        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, bounds, Theme.Muted, Flags | alignment);
    }

    /// <summary>
    /// Each sub-item fills its own cell — painting the whole row here would wipe sub-items that
    /// this paint pass isn't going to redraw. Only the strip past the last column is left, and it
    /// does have to be painted, because the background erase is suppressed (see <see cref="WndProc"/>).
    /// </summary>
    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        var columns = 0;
        foreach (ColumnHeader column in Columns)
            columns += column.Width;

        var left = e.Bounds.X + columns;
        if (left >= e.Bounds.Right)
            return;

        using var background = new SolidBrush(Theme.Panel);
        e.Graphics.FillRectangle(background, left, e.Bounds.Y, e.Bounds.Right - left, e.Bounds.Height);
    }

    /// <summary>
    /// Swallows the background erase, and the request for a menu where there is no task to put one
    /// on.
    /// </summary>
    /// <remarks>
    /// The control repaints a row as the pointer crosses it, and erasing first leaves it blank for a
    /// frame — the flicker under the mouse. Everything is painted by the draw handlers, so there is
    /// nothing the erase needs to do.
    /// </remarks>
    protected override void WndProc(ref Message m)
    {
        const int WmEraseBackground = 0x0014;
        const int WmContextMenu = 0x007B;

        if (m.Msg == WmEraseBackground)
        {
            m.Result = 1;
            return;
        }

        // A menu asked for with the keyboard is about the selected row, wherever that has got to on
        // screen. One asked for with the mouse is about the row under the pointer — and below the
        // last task there is no row for it to be about, so the click gets nothing rather than a menu
        // aimed at whichever row was selected somewhere else. The two are told apart by lParam,
        // which the keyboard sends as -1.
        if (m.Msg == WmContextMenu && m.LParam != -1 && !PointsAtRow(m.LParam))
            return;

        base.WndProc(ref m);
    }

    /// <summary>Whether a screen position packed into an lParam is over a row.</summary>
    private bool PointsAtRow(nint lParam)
    {
        // Signed: a second monitor to the left of the main one puts the pointer at a negative x.
        var screen = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
        return HitTest(PointToClient(screen)).Item?.Index is { } index && index >= 0 && index < _rows.Count;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.ItemIndex >= _rows.Count)
            return;

        var row = _rows[e.ItemIndex];

        // Not e.ItemState: in virtual owner-draw mode its Selected flag is unreliable for sub-items,
        // which painted rows as selected simply because the mouse passed over them.
        var selected = IsSelected(e.ItemIndex);

        // A completed row is greyed and struck through: it is here to be seen and reopened, not
        // read alongside the work that is still outstanding.
        var text = selected ? Theme.OnAccent : row.Completed ? Theme.Muted : Theme.Text;
        var muted = selected ? Theme.OnAccent : Theme.Muted;
        var font = row.Completed ? Struck : Font;

        using (var background = new SolidBrush(selected ? Theme.Accent : Theme.Panel))
            e.Graphics.FillRectangle(background, e.Bounds);

        switch (e.ColumnIndex)
        {
            case 0:
                var bounds = e.Bounds;
                bounds.X += row.Depth * IndentWidth;
                bounds.Width -= row.Depth * IndentWidth;
                DrawGuides(e.Graphics, e.Bounds, row.Depth, selected ? Theme.OnAccent : Theme.Border);
                TextRenderer.DrawText(e.Graphics, ContentOf(row), font, Inset(bounds), text, Flags);
                break;

            case 1:
                DrawPriority(e.Graphics, e.Bounds, row.Priority);
                break;

            case 2:
                TextRenderer.DrawText(e.Graphics, row.Project, Font, Inset(e.Bounds), muted, Flags);
                break;

            case 3:
                TextRenderer.DrawText(e.Graphics, DueOf(row), Font, Inset(e.Bounds), muted, Flags);
                break;

            case 4:
                TextRenderer.DrawText(e.Graphics, LabelsOf(row), Font, Inset(e.Bounds), muted, Flags);
                break;
        }
    }

    /// <summary>Labels as they are written in quick-add, so the row reads the way it was typed.</summary>
    private static string LabelsOf(TaskRow row)
        => row.Labels.Count == 0 ? string.Empty : "@" + string.Join(" @", row.Labels);

    /// <summary>
    /// The task's own column: its name, and a mark when there's a conversation on it.
    /// </summary>
    /// <remarks>
    /// Marked here rather than beside the repeat and the reminder, which share the due column
    /// because both are about when the task comes round. A comment isn't about timing at all, and
    /// without a mark somewhere it's invisible until you open the pane.
    /// </remarks>
    private static string ContentOf(TaskRow row)
        => row.CommentCount > 0 ? $"{row.Content}  💬" : row.Content;

    /// <summary>
    /// The due column. A repeat and a reminder are marked here rather than given columns of their
    /// own: both are about when the task comes round, and neither is worth the width.
    /// </summary>
    private static string DueOf(TaskRow row)
    {
        var marks = (row.IsRecurring ? "↻" : string.Empty) + (row.ReminderCount > 0 ? "⏰" : string.Empty);
        if (marks.Length == 0)
            return row.Due;

        return row.Due.Length == 0 ? marks : $"{marks} {row.Due}";
    }

    /// <summary>Faint vertical rules showing how deep a sub-task sits.</summary>
    private static void DrawGuides(Graphics g, Rectangle bounds, int depth, Color colour)
    {
        if (depth == 0)
            return;

        using var pen = new Pen(colour);
        for (var level = 0; level < depth; level++)
        {
            var x = bounds.X + (level * IndentWidth) + (IndentWidth / 2);
            g.DrawLine(pen, x, bounds.Top, x, bounds.Bottom);
        }
    }

    private static void DrawPriority(Graphics g, Rectangle bounds, Priority priority)
    {
        if (priority == Priority.P4)
            return;

        var colour = Theme.ForPriority(priority);

        var size = Math.Min(9, bounds.Height - 8);
        var dot = new Rectangle(
            bounds.X + ((bounds.Width - size) / 2),
            bounds.Y + ((bounds.Height - size) / 2),
            size,
            size);

        using var brush = new SolidBrush(colour);
        g.FillEllipse(brush, dot);
    }

    private static TextFormatFlags Flags
        => TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private static Rectangle Inset(Rectangle bounds)
        => new(bounds.X + TextInset, bounds.Y, Math.Max(0, bounds.Width - (TextInset * 2)), bounds.Height);
}
