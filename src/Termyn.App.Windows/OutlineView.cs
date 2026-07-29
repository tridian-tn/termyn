using System.ComponentModel;
using Termyn.Core.Model;
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

    private static readonly Color P1 = Color.FromArgb(0xE4, 0x48, 0x3A);
    private static readonly Color P2 = Color.FromArgb(0xF5, 0xA6, 0x23);
    private static readonly Color P3 = Color.FromArgb(0x3B, 0x82, 0xF6);

    private IReadOnlyList<TaskRow> _rows = [];

    /// <summary>
    /// Virtual mode asks for the same row repeatedly — on every hover, focus change and repaint —
    /// and expects the same instance back each time. Handing out a fresh one makes the control
    /// re-evaluate item state and the selection follows the mouse.
    /// </summary>
    private ListViewItem?[] _cache = [];

    /// <summary>Cached so painting doesn't ask the native control once per cell.</summary>
    private int _selectedIndex = -1;

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
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        DoubleBuffered = true;

        Columns.Add("Task", 360);
        Columns.Add("!", 34, HorizontalAlignment.Center);
        Columns.Add("Project", 140);
        Columns.Add("Due", 120);
        Columns.Add("Labels", 140);
    }

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

            // Put the selection back on the same task, if it's still here. Deliberately no
            // scrolling and no fallback selection: a background sync refreshes these rows every
            // 45 seconds, and it must not move the selection or the viewport under the user.
            var index = selected is null ? -1 : IndexOf(selected);
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

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        _selectedIndex = SelectedIndices.Count > 0 ? SelectedIndices[0] : -1;
        base.OnSelectedIndexChanged(e);
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
            cached = new ListViewItem([row.Content, string.Empty, row.Project, DueOf(row), LabelsOf(row)]) { Tag = row.Id };
            _cache[e.ItemIndex] = cached;
        }

        e.Item = cached;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawBackground();

        var alignment = e.Header?.TextAlign switch
        {
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            HorizontalAlignment.Right => TextFormatFlags.Right,
            _ => TextFormatFlags.Left,
        };

        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, Inset(e.Bounds), SystemColors.GrayText, Flags | alignment);
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

        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, left, e.Bounds.Y, e.Bounds.Right - left, e.Bounds.Height);
    }

    /// <summary>
    /// Swallows the background erase. The control repaints a row as the pointer crosses it, and
    /// erasing first leaves it blank for a frame — the flicker under the mouse. Everything is
    /// painted by the draw handlers, so there is nothing the erase needs to do.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        const int WmEraseBackground = 0x0014;

        if (m.Msg == WmEraseBackground)
        {
            m.Result = 1;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.ItemIndex >= _rows.Count)
            return;

        var row = _rows[e.ItemIndex];

        // Not e.ItemState: in virtual owner-draw mode its Selected flag is unreliable for sub-items,
        // which painted rows as selected simply because the mouse passed over them.
        var selected = IsSelected(e.ItemIndex);
        var text = selected ? SystemColors.HighlightText : ForeColor;
        var muted = selected ? SystemColors.HighlightText : SystemColors.GrayText;

        using (var background = new SolidBrush(selected ? SystemColors.Highlight : BackColor))
            e.Graphics.FillRectangle(background, e.Bounds);

        switch (e.ColumnIndex)
        {
            case 0:
                var bounds = e.Bounds;
                bounds.X += row.Depth * IndentWidth;
                bounds.Width -= row.Depth * IndentWidth;
                DrawGuides(e.Graphics, e.Bounds, row.Depth, selected);
                TextRenderer.DrawText(e.Graphics, row.Content, Font, Inset(bounds), text, Flags);
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
    /// The due column. A repeat and a reminder are marked here rather than given columns of their
    /// own: both are about when the task comes round, and neither is worth the width.
    /// </summary>
    private static string DueOf(TaskRow row)
    {
        var marks = (row.IsRecurring ? "↻" : string.Empty) + (row.Reminders > 0 ? "⏰" : string.Empty);
        if (marks.Length == 0)
            return row.Due;

        return row.Due.Length == 0 ? marks : $"{marks} {row.Due}";
    }

    /// <summary>Faint vertical rules showing how deep a sub-task sits.</summary>
    private static void DrawGuides(Graphics g, Rectangle bounds, int depth, bool selected)
    {
        if (depth == 0)
            return;

        using var pen = new Pen(selected ? SystemColors.HighlightText : SystemColors.ControlLight);
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

        var colour = priority switch
        {
            Priority.P1 => P1,
            Priority.P2 => P2,
            _ => P3,
        };

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
