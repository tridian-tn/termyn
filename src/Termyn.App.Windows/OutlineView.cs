using System.ComponentModel;
using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The task outline: a virtual, owner-drawn list. Rows are drawn rather than composed from controls
/// so that indentation, the priority flag and the label chips cost nothing per row, and only the
/// visible rows are ever realised.
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

        Columns.Add("Task", 420);
        Columns.Add("!", 34, HorizontalAlignment.Center);
        Columns.Add("Project", 150);
        Columns.Add("Due", 130);
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
            Invalidate();

            if (selected is not null)
                SelectId(selected);
            else if (_rows.Count > 0 && SelectedIndices.Count == 0)
                SelectIndex(0);
        }
    }

    public string? SelectedId
        => SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count ? _rows[SelectedIndices[0]].Id : null;

    public TaskRow? SelectedRow
        => SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count ? _rows[SelectedIndices[0]] : null;

    public void SelectId(string id)
    {
        var index = -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Id != id)
                continue;
            index = i;
            break;
        }

        if (index >= 0)
            SelectIndex(index);
    }

    private void SelectIndex(int index)
    {
        SelectedIndices.Clear();
        SelectedIndices.Add(index);
        Items[index].Focused = true;
        EnsureVisible(index);
    }

    // Tab and Shift+Tab indent and outdent here rather than moving focus out of the list.
    protected override bool IsInputKey(Keys keyData)
        => (keyData & Keys.KeyCode) == Keys.Tab || base.IsInputKey(keyData);

    protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex >= _cache.Length)
        {
            e.Item = new ListViewItem();
            return;
        }

        if (_cache[e.ItemIndex] is not { } cached)
        {
            var row = _rows[e.ItemIndex];
            cached = new ListViewItem([row.Content, string.Empty, row.Project, row.Due]) { Tag = row.Id };
            _cache[e.ItemIndex] = cached;
        }

        e.Item = cached;
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        e.DrawBackground();
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, Inset(e.Bounds), SystemColors.GrayText, Flags);
    }

    /// <summary>
    /// Nothing to do in Details view: painting the row here would wipe sub-items that this paint
    /// pass isn't going to redraw, which is what blanked cells as the mouse moved over them.
    /// Each sub-item fills its own background instead.
    /// </summary>
    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.ItemIndex >= _rows.Count)
            return;

        var row = _rows[e.ItemIndex];
        var selected = (e.ItemState & ListViewItemStates.Selected) != 0;
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
                TextRenderer.DrawText(e.Graphics, row.Due, Font, Inset(e.Bounds), muted, Flags);
                break;
        }
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
