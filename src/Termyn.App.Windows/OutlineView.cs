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

    /// <summary>
    /// How much room the expander takes at the head of a row.
    /// </summary>
    /// <remarks>
    /// Kept on every row and not only the ones that have an expander, so a task with sub-tasks and
    /// one without still start their words in the same place. A gutter that appeared and vanished
    /// would shuffle the whole column sideways as tasks gained and lost children.
    /// </remarks>
    private const int ExpanderWidth = 14;

    /// <summary>How big the arrow itself is drawn inside that room.</summary>
    private const int ExpanderGlyph = 8;

    /// <summary>
    /// How much room the box that ticks a task off takes, between the expander and the words.
    /// </summary>
    /// <remarks>
    /// Wider than the box drawn in it, and the whole of it answers to a click: a fourteen-pixel
    /// square is a target people miss, and the room around it costs nothing to hand over.
    /// </remarks>
    private const int CheckWidth = 20;

    /// <summary>How big the box itself is drawn inside that room.</summary>
    private const int CheckSize = 13;

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
        Columns.Add("Deadline", 100).Tag = TaskColumn.Deadline;
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
    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value;

            // The pen is the one colour kept rather than taken per draw, so it goes with the
            // theme that chose it.
            _rule?.Dispose();
            _rule = null;
        }
    }

    private Theme _theme = Theme.Resolve(ThemePreference.System);

    protected override void OnFontChanged(EventArgs e)
    {
        _struck?.Dispose();
        _struck = null;
        _heading?.Dispose();
        _heading = null;
        base.OnFontChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _struck?.Dispose();
            _heading?.Dispose();
            _rule?.Dispose();
        }

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

            // Forgotten with the rows it counted. A sync refreshes these every 45 seconds, and an
            // index left over from the last lot names a different task — which would send the next
            // step off a heading the wrong way.
            _lastOnTask = -1;

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

    public string? SelectedId => SelectedRow?.Id;

    /// <summary>
    /// The task the list is on, or null when it is on none.
    /// </summary>
    /// <remarks>
    /// A day's heading answers null. The selection is moved off one as soon as it lands there, so
    /// this is the second line rather than the first — but everything that acts on a task reads
    /// the selection through here, and none of it should ever be handed a row that isn't one.
    /// </remarks>
    public TaskRow? SelectedRow
        => SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count && !_rows[SelectedIndices[0]].IsHeading
            ? _rows[SelectedIndices[0]]
            : null;

    /// <summary>
    /// Selects a task and scrolls it into view. For explicit navigation, not for refreshes.
    /// </summary>
    /// <remarks>
    /// The focus goes with the selection. A list moves from wherever its focus is, so a row picked
    /// out from somewhere else — a search, the palette, the tray — would otherwise spend the first
    /// arrow key afterwards bringing the focus back to it, and that press would look thrown away.
    /// </remarks>
    /// <param name="id">The task to select</param>
    public void SelectId(string id)
    {
        if (IndexOf(id) is var index and >= 0)
        {
            SelectedIndices.Clear();
            SelectedIndices.Add(index);
            FocusedItem = Items[index];
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
        // Held while the selection is being moved off a heading; that path publishes once when it
        // has landed on a task.
        if (_stepping)
            return;

        var index = SelectedIndices.Count > 0 ? SelectedIndices[0] : -1;

        // A day's heading is not a task, so the selection carries on past it the way it arrived —
        // down onto the day's first task, or up onto the last of the day before.
        if (index >= 0 && index < _rows.Count && _rows[index].IsHeading && PastHeading(index) is { } landed)
            index = Step(landed);

        _selectedIndex = index;

        // Which way the next arrival is travelling is measured from here rather than from the row
        // last selected: moving a selection clears it first, and that empty moment would otherwise
        // read as having come from the top of the list every time.
        if (index >= 0 && index < _rows.Count && !_rows[index].IsHeading)
            _lastOnTask = index;

        // Held while the rows are being reassigned; that path publishes once when it is done.
        if (_reseating)
            return;

        _publishedIndex = _selectedIndex;
        base.OnSelectedIndexChanged(e);
    }

    /// <summary>True while the selection is being walked off a heading, so the move is published once.</summary>
    private bool _stepping;

    /// <summary>The last row the selection settled on that was a task, which says which way it moves.</summary>
    private int _lastOnTask = -1;

    /// <summary>
    /// The row to carry a selection on to, having landed on a heading.
    /// </summary>
    /// <remarks>
    /// The way it was already going, so an arrow key keeps its direction and a click on a heading
    /// takes the day it heads. Turned round at either end, where carrying on would mean leaving
    /// the list — the top of Upcoming is a heading, and arriving there from below has to land on
    /// something.
    /// </remarks>
    /// <param name="index">The heading the selection landed on</param>
    /// <returns>The row to take instead, or null when there is no task either way</returns>
    private int? PastHeading(int index)
    {
        var forwards = index > _lastOnTask;

        return Task(index, forwards ? 1 : -1) ?? Task(index, forwards ? -1 : 1);

        int? Task(int from, int step)
        {
            for (var at = from + step; at >= 0 && at < _rows.Count; at += step)
                if (!_rows[at].IsHeading)
                    return at;

            return null;
        }
    }

    /// <summary>
    /// Moves the selection, without publishing the half of it that lands nowhere.
    /// </summary>
    /// <remarks>
    /// The focus goes with it. A list moves its selection from wherever the focus is, so leaving
    /// the focus on the heading costs the next keypress: it steps the focus onto the row that is
    /// already selected, the selection doesn't move, and the key reads as having been swallowed.
    /// </remarks>
    /// <param name="index">The row to select</param>
    /// <returns>The row now selected</returns>
    private int Step(int index)
    {
        _stepping = true;
        try
        {
            SelectedIndices.Clear();
            SelectedIndices.Add(index);
            FocusedItem = Items[index];
        }
        finally
        {
            _stepping = false;
        }

        return index;
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
        // Neither the box nor the expander is the row. Clicking one asks for that one thing and
        // nothing else — the selection stays where the user put it, which is what every list does
        // and what stops ticking a task off from moving them away from what they were reading.
        if (e.Button == MouseButtons.Left && CheckboxAt(e.Location) is { } ticked)
        {
            Focus();
            ToggleRequested?.Invoke(ticked);
            return;
        }

        if (e.Button == MouseButtons.Left && ExpanderAt(e.Location) is { } id)
        {
            Focus();
            CollapseRequested?.Invoke(id, !IsCollapsed(id));
            return;
        }

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

    /// <summary>Asks for a task's sub-tasks to be hidden, or shown again.</summary>
    /// <remarks>The task's id, and whether it should end up collapsed.</remarks>
    public event Action<string, bool>? CollapseRequested;

    /// <summary>Asks for a task to be ticked off, or put back — the box on its row was clicked.</summary>
    public event Action<string>? ToggleRequested;

    /// <summary>Clicks a task's box, for a test with no row to click.</summary>
    /// <param name="id">The task whose box was clicked</param>
    internal void ClickCheckbox(string id) => ToggleRequested?.Invoke(id);

    /// <summary>
    /// Presses a mouse button somewhere in the list, for a test with no hand to do it.
    /// </summary>
    /// <remarks>
    /// Straight to the handler rather than through a posted button-down: a list takes the mouse on
    /// a press and waits inside its own loop for the release, and a release nobody sends never
    /// arrives — which hangs the test rather than failing it.
    /// </remarks>
    /// <param name="button">Which button went down</param>
    /// <param name="at">Where, in the list's own coordinates</param>
    internal void PressAt(MouseButtons button, Point at) => OnMouseDown(new MouseEventArgs(button, 1, at.X, at.Y, 0));

    /// <summary>
    /// The task whose box is under a point, or null where there isn't one.
    /// </summary>
    /// <remarks>
    /// The whole gutter answers rather than the square drawn in it, for the reason the expander's
    /// does: a thirteen-pixel target is one people miss. A day's heading has no box — there's no
    /// task on that row to tick off.
    /// </remarks>
    /// <param name="point">Where the pointer is, in the list's own coordinates</param>
    /// <returns>The task's id, or null when that point is not a box</returns>
    internal string? CheckboxAt(Point point)
    {
        if (HitTest(point).Item?.Index is not { } index || index < 0 || index >= _rows.Count)
            return null;

        var row = _rows[index];
        if (row.IsHeading)
            return null;

        // The first column's own left edge, since a list can be scrolled sideways and the columns
        // can be dragged narrower than the indent they are holding.
        var bounds = GetItemRect(index, ItemBoundsPortion.Entire);
        bounds.X += (row.Depth * IndentWidth) + ExpanderWidth;

        return new Rectangle(bounds.X, bounds.Y, CheckWidth, bounds.Height).Contains(point) ? row.Id : null;
    }

    /// <summary>
    /// The task whose expander is under a point, or null where there isn't one.
    /// </summary>
    /// <remarks>
    /// The whole gutter answers rather than the arrow drawn in it. The arrow is eight pixels and a
    /// target that small is one people miss, so the room it sits in is the target.
    /// </remarks>
    /// <param name="point">Where the pointer is, in the list's own coordinates</param>
    /// <returns>The task's id, or null when that point is not an expander</returns>
    internal string? ExpanderAt(Point point)
    {
        if (HitTest(point).Item?.Index is not { } index || index < 0 || index >= _rows.Count)
            return null;

        var row = _rows[index];
        if (!row.HasChildren)
            return null;

        // The first column's own left edge, since a list can be scrolled sideways and the columns
        // can be dragged narrower than the indent they are holding.
        var bounds = GetItemRect(index, ItemBoundsPortion.Entire);
        bounds.X += row.Depth * IndentWidth;

        return Expander(bounds).Contains(point) ? row.Id : null;
    }

    /// <summary>Whether the row for a task is currently collapsed, as the last rows said.</summary>
    private bool IsCollapsed(string id)
    {
        foreach (var row in _rows)
            if (row.Id == id)
                return row.Collapsed;

        return false;
    }

    /// <summary>
    /// Left and Right fold the selected task, the way they do in a tree.
    /// </summary>
    /// <remarks>
    /// Only where there is something to fold: a task with no sub-tasks, or one already the way the
    /// key would put it, leaves the key alone rather than swallowing it. A list in Details view
    /// does nothing with Left and Right of its own, but a key quietly eaten by whatever happens to
    /// have the focus is the sort of thing nobody can account for later.
    ///
    /// Taken here rather than from the key event, because an arrow is a navigation key: it is
    /// offered around before the control is given it, and by the time this one had it the list had
    /// already decided the key was none of its business.
    /// </remarks>
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        => Fold(keyData) || base.ProcessCmdKey(ref message, keyData);

    /// <summary>
    /// Folds the selected row if that is what the keystroke asks for.
    /// </summary>
    /// <remarks>
    /// Split out from the override so a test can say which keystroke it means. Going in through
    /// PreProcessMessage instead means going in through <c>(Keys)msg.WParam | ModifierKeys</c> —
    /// the real keyboard — so a Ctrl held anywhere on the machine at that instant turns Left into
    /// Ctrl+Left and the control answers a question nobody asked.
    /// </remarks>
    /// <param name="keyData">The keystroke, modifiers and all</param>
    /// <returns>Whether it was this control's to answer</returns>
    internal bool Fold(Keys keyData)
    {
        if (!FoldsOn(keyData)
            || SelectedRow is not { HasChildren: true } row
            || row.Collapsed == (keyData == Keys.Left))
        {
            return false;
        }

        CollapseRequested?.Invoke(row.Id, keyData == Keys.Left);
        return true;
    }

    /// <summary>
    /// Whether a keystroke is one of the two that fold a row.
    /// </summary>
    /// <remarks>
    /// The arrows on their own and no modified form of them: Ctrl and an arrow indents the task or
    /// moves it, and folding the row as well would make one keystroke do two things. Its own method
    /// so a test can ask, since the alternative is holding Ctrl down on a build agent.
    /// </remarks>
    /// <param name="keyData">The keystroke, modifiers and all</param>
    /// <returns>Whether it asks for a fold</returns>
    internal static bool FoldsOn(Keys keyData) => keyData is Keys.Left or Keys.Right;

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
            cached = new ListViewItem(Cells(row)) { Tag = row.Id };
            _cache[e.ItemIndex] = cached;
        }

        e.Item = cached;
    }

    /// <summary>
    /// A row's cells in the order the columns stand, which is what the control is handed.
    /// </summary>
    /// <param name="row">The task to read across</param>
    /// <returns>One string per column, empty for the ones drawn rather than written</returns>
    internal string[] Cells(TaskRow row)
    {
        var cells = new string[Columns.Count];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = Columns[i].Tag is TaskColumn column ? CellOf(row, column) : string.Empty;

        return cells;
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
        if (!Buffered(e.Bounds))
            return;

        var columns = 0;
        foreach (ColumnHeader column in Columns)
            columns += column.Width;

        var left = e.Bounds.X + columns;
        if (left >= e.Bounds.Right)
            return;

        using var background = new SolidBrush(Theme.Panel);
        e.Graphics.FillRectangle(background, left, e.Bounds.Y, e.Bounds.Right - left, e.Bounds.Height);
    }

    /// <summary>True while a paint is being served, which is the only drawing that is buffered.</summary>
    private bool _painting;

    /// <summary>How much has been drawn through a paint, and how much was turned away to become one.</summary>
    /// <remarks>
    /// Internal because the fault this control had is invisible from the outside: it drew the right
    /// pixels either way, and what was wrong was which side of a paint it drew them on. A test
    /// crossing the rows with the pointer holds the first of these above nought — refusing to draw
    /// at all would leave the flicker gone and the rows blank, and both readings tell those apart.
    /// </remarks>
    internal int DrawnInPaint { get; private set; }

    /// <summary>How many draws were refused and asked for again as a paint.</summary>
    internal int AskedOutsidePaint { get; private set; }

    /// <summary>Forgets the counts, so a test can measure one stretch of drawing rather than all of it.</summary>
    internal void ForgetDrawCounts() => (DrawnInPaint, AskedOutsidePaint) = (0, 0);

    /// <summary>
    /// Notes which side of a paint the drawing is on, and swallows the background erase and the
    /// request for a menu where there is no task to put one on.
    /// </summary>
    /// <remarks>
    /// The control repaints a row as the pointer crosses it, and erasing first leaves it blank for a
    /// frame — the flicker under the mouse. Everything is painted by the draw handlers, so there is
    /// nothing the erase needs to do.
    ///
    /// Whether a draw is inside a paint is not something the draw itself carries, and it is the
    /// whole of the other half of that flicker, so it is noted here. See <see cref="Buffered"/>.
    /// </remarks>
    protected override void WndProc(ref Message m)
    {
        const int WmEraseBackground = 0x0014;
        const int WmContextMenu = 0x007B;
        const int WmPaint = 0x000F;
        const int WmLeftDown = 0x0201;
        const int WmLeftDouble = 0x0203;
        const int WmRightDown = 0x0204;

        if (m.Msg == WmPaint)
        {
            _painting = true;
            try
            {
                base.WndProc(ref m);
            }
            finally
            {
                _painting = false;
            }

            return;
        }

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

        // A day's heading is not a task, so a click on one is dropped where it lands. Letting the
        // list select it and moving the selection off afterwards works, but the row it settles on
        // lights up and goes out again — a flash on a row nobody clicked.
        if (m.Msg is WmLeftDown or WmLeftDouble or WmRightDown && OverHeading(m.LParam))
            return;

        base.WndProc(ref m);
    }

    /// <summary>
    /// Whether this draw will reach the screen through the buffer, and asks for one that will when
    /// it won't.
    /// </summary>
    /// <remarks>
    /// The list redraws a row the first time the pointer enters it, and that redraw does not come
    /// through a paint — it is drawn straight onto the window. Double buffering only covers a
    /// paint, which is why this control has had it all along and flickered anyway: the row's draws,
    /// the item and then each of its cells, land one at a time and the row is watched being
    /// assembled. What it draws is identical to what is already there, which is why it reads as a
    /// flicker rather than as a change.
    ///
    /// So nothing is drawn outside a paint. The row is asked for again as one instead, which comes
    /// back buffered and puts it up in a single go. Asking costs a paint that would not otherwise
    /// have happened, and it happens once per row until something redraws the list.
    /// </remarks>
    /// <param name="bounds">What the draw would have covered</param>
    /// <returns>True to go ahead and draw</returns>
    private bool Buffered(Rectangle bounds)
    {
        if (_painting)
        {
            DrawnInPaint++;
            return true;
        }

        AskedOutsidePaint++;
        Invalidate(bounds);
        return false;
    }

    /// <summary>
    /// Whether a click position packed into an lParam is over a day's heading.
    /// </summary>
    /// <remarks>
    /// Client coordinates, unlike the context menu's, which the shell sends in screen ones.
    /// </remarks>
    /// <param name="lParam">Where the button went down, as the message carries it</param>
    /// <returns>True when that is a heading rather than a task</returns>
    private bool OverHeading(nint lParam) => IsHeadingAt(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));

    /// <summary>Whether a point in the list is over a day's heading.</summary>
    /// <param name="client">Where to look, in the list's own coordinates</param>
    /// <returns>True when a heading is drawn there</returns>
    internal bool IsHeadingAt(Point client)
        => HitTest(client).Item?.Index is { } index && index >= 0 && index < _rows.Count && _rows[index].IsHeading;

    /// <summary>Whether a screen position packed into an lParam is over a row.</summary>
    private bool PointsAtRow(nint lParam)
    {
        // Signed: a second monitor to the left of the main one puts the pointer at a negative x.
        var screen = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
        return HitTest(PointToClient(screen)).Item?.Index is { } index && index >= 0 && index < _rows.Count;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (!Buffered(e.Bounds))
            return;

        if (e.ItemIndex >= _rows.Count || e.ColumnIndex < 0 || e.ColumnIndex >= Columns.Count)
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

        // Which column this is comes from the header's own tag rather than its position, so adding
        // one can't leave a cell drawn under the wrong heading.
        if (Columns[e.ColumnIndex].Tag is not TaskColumn column)
            return;

        if (row.IsHeading)
        {
            DrawDay(e.Graphics, e.Bounds, row, column, selected ? Theme.OnAccent : Theme.Text);
            return;
        }

        switch (PaintOf(column))
        {
            case CellPaint.Priority:
                DrawPriority(e.Graphics, e.Bounds, row.Priority);
                break;

            case CellPaint.Project:
                DrawProject(e.Graphics, e.Bounds, row, selected, muted);
                break;

            case CellPaint.Labels:
                DrawLabels(e.Graphics, e.Bounds, row, selected, muted);
                break;

            // The task's own column is words like the others, but they start after the indent and
            // the expander rather than at the edge of the cell.
            case CellPaint.Written when column == TaskColumn.Content:
                var bounds = e.Bounds;
                bounds.X += row.Depth * IndentWidth;
                bounds.Width -= row.Depth * IndentWidth;
                DrawGuides(e.Graphics, e.Bounds, row.Depth, selected ? Theme.OnAccent : Theme.Border);

                // The words start after the expander whether or not there is one to draw.
                if (row.HasChildren)
                    DrawExpander(e.Graphics, Expander(bounds), row.Collapsed, selected ? Theme.OnAccent : Theme.Muted);

                bounds.X += ExpanderWidth;
                bounds.Width -= ExpanderWidth;

                DrawCheck(e.Graphics, Checkbox(bounds), row.Completed, selected ? Theme.OnAccent : Theme.Muted);

                bounds.X += CheckWidth;
                bounds.Width -= CheckWidth;
                TextRenderer.DrawText(e.Graphics, CellOf(row, column), font, Inset(bounds), text, Flags);
                break;

            // The same face the task's own column uses, so a finished task's dates are struck
            // through with its name rather than left reading as though they still stood.
            default:
                TextRenderer.DrawText(e.Graphics, CellOf(row, column), font, Inset(e.Bounds), muted, Flags);
                break;
        }
    }

    /// <summary>The face a day's heading is set in, made on first use like the struck one.</summary>
    private Font Heading => _heading ??= new Font(Font, FontStyle.Bold);

    private Font? _heading;

    /// <summary>
    /// Draws the row that heads a day's tasks: a rule across the list, and the day above it.
    /// </summary>
    /// <remarks>
    /// The rule is drawn a cell at a time, since that is how the list hands the row out — each
    /// cell's share of it lines up with the next to make one line across the width. The day itself
    /// is written in the task column, which is the widest and the one the eye starts at.
    /// </remarks>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The cell being drawn</param>
    /// <param name="row">The heading row</param>
    /// <param name="column">Which column this cell belongs to</param>
    /// <param name="colour">What to write the day in, which the selection changes</param>
    private void DrawDay(Graphics g, Rectangle bounds, TaskRow row, TaskColumn column, Color colour)
    {
        g.DrawLine(Rule, bounds.Left, bounds.Top, bounds.Right, bounds.Top);

        if (column != TaskColumn.Content)
            return;

        TextRenderer.DrawText(g, row.Content, Heading, Inset(bounds), colour, Flags);
    }

    /// <summary>
    /// The line drawn above a day's heading, held like the fonts rather than made per cell.
    /// </summary>
    /// <remarks>
    /// A heading is handed out a cell at a time and redrawn whenever the pointer crosses the row,
    /// so a pen built per call is six of them per pass over one row.
    /// </remarks>
    private Pen Rule => _rule ??= new Pen(Theme.Border);

    private Pen? _rule;

    /// <summary>How the outline fills a cell: with words, or with one of the marks it paints.</summary>
    internal enum CellPaint
    {
        /// <summary>Written out, which is what a column is unless it's one of the three below.</summary>
        Written,

        Priority,
        Project,
        Labels,
    }

    /// <summary>
    /// Which of those a column gets.
    /// </summary>
    /// <remarks>
    /// Kept out of the drawing so a test can hold it to this. The paint itself can't be asserted —
    /// a virtual owner-drawn list won't render its rows into a bitmap — so a column that quietly
    /// stopped being painted would go on writing the same words with the colour gone, and nothing
    /// would fail. Here, dropping one is a test away.
    /// </remarks>
    /// <param name="column">The column being drawn</param>
    /// <returns>What fills its cells</returns>
    internal static CellPaint PaintOf(TaskColumn column) => column switch
    {
        TaskColumn.Priority => CellPaint.Priority,
        TaskColumn.Project => CellPaint.Project,
        TaskColumn.Labels => CellPaint.Labels,
        _ => CellPaint.Written,
    };

    /// <summary>
    /// What a cell says, for the columns that are words rather than marks.
    /// </summary>
    /// <remarks>
    /// One table for the text the control is handed and the text that's drawn, so a column can't
    /// end up saying one thing to the screen and another to a screen reader. The priority column
    /// is a flag with no words to it, and answers empty.
    /// </remarks>
    /// <param name="row">The task the cell belongs to</param>
    /// <param name="column">Which of its columns is being asked for</param>
    /// <returns>The words for that cell, or empty when the column is drawn rather than written</returns>
    internal static string CellOf(TaskRow row, TaskColumn column) => column switch
    {
        TaskColumn.Content => ContentOf(row),
        TaskColumn.Project => row.Project,
        TaskColumn.Due => DueOf(row),
        TaskColumn.Deadline => row.Deadline,
        TaskColumn.Labels => LabelsOf(row),
        _ => string.Empty,
    };

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

    /// <summary>Where the expander sits, given the row's first column already moved in by its depth.</summary>
    private static Rectangle Expander(Rectangle indented)
        => new(indented.X, indented.Y, ExpanderWidth, indented.Height);

    /// <summary>
    /// Where the box that ticks a task off sits, given the row already moved past its expander.
    /// </summary>
    /// <param name="indented">The first column, indented and past the expander's room</param>
    /// <returns>The square the box is drawn in, centred in the room it's given</returns>
    internal static Rectangle Checkbox(Rectangle indented)
    {
        var size = Math.Min(CheckSize, Math.Max(0, indented.Height - 4));

        return new Rectangle(
            indented.X + ((CheckWidth - size) / 2),
            indented.Y + ((indented.Height - size) / 2),
            size,
            size);
    }

    /// <summary>
    /// Draws the box that ticks a task off: a square, and a tick in it once it's done.
    /// </summary>
    /// <remarks>
    /// Drawn rather than asked of Windows. A themed checkbox comes out in the system's colours,
    /// which on the dark theme is a pale box on a dark row — and the tick is three points, which
    /// is less code than fetching the renderer and correcting what it gives back.
    /// </remarks>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The square the box fills</param>
    /// <param name="ticked">Whether the task is done</param>
    /// <param name="colour">What to draw it in</param>
    internal static void DrawCheck(Graphics g, Rectangle bounds, bool ticked, Color colour)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        using var pen = new Pen(colour);

        // The square is four straight lines, which smoothing only softens; the tick is diagonal,
        // and unsmoothed at this size it reads as a staircase.
        g.DrawRectangle(pen, bounds);

        if (!ticked)
            return;

        var was = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        try
        {
            var inset = Math.Max(2, bounds.Width / 5);
            var foot = new Point(bounds.Left + inset, bounds.Top + (bounds.Height / 2));
            var turn = new Point(bounds.Left + (bounds.Width / 2) - 1, bounds.Bottom - inset - 1);
            var head = new Point(bounds.Right - inset, bounds.Top + inset);

            g.DrawLines(pen, [foot, turn, head]);
        }
        finally
        {
            g.SmoothingMode = was;
        }
    }

    /// <summary>
    /// The arrow that hides and shows a task's sub-tasks — right when they are hidden, down when
    /// they are not.
    /// </summary>
    /// <remarks>
    /// Drawn rather than written. A glyph would mean a font that has it, which is a thing to check
    /// for and fall back from, and three points are cheaper than either. Filled rather than outlined
    /// so it reads at this size in both themes.
    /// </remarks>
    /// <param name="g">What to draw on</param>
    /// <param name="bounds">The room the expander has</param>
    /// <param name="collapsed">Whether the sub-tasks are currently hidden</param>
    /// <param name="colour">What to draw it in</param>
    private static void DrawExpander(Graphics g, Rectangle bounds, bool collapsed, Color colour)
    {
        var size = Math.Min(ExpanderGlyph, Math.Min(bounds.Width, bounds.Height));
        var left = bounds.X + ((bounds.Width - size) / 2);
        var top = bounds.Y + ((bounds.Height - size) / 2);

        var arrow = collapsed
            ? new[] { new Point(left, top), new Point(left + size, top + (size / 2)), new Point(left, top + size) }
            : new[] { new Point(left, top), new Point(left + size, top), new Point(left + (size / 2), top + size) };

        // Smoothed, since the diagonals are the whole shape and unsmoothed they read as a staircase.
        var was = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using (var brush = new SolidBrush(colour))
            g.FillPolygon(brush, arrow);

        g.SmoothingMode = was;
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

    /// <summary>
    /// What each label is coloured with, by name.
    /// </summary>
    /// <remarks>
    /// Given rather than looked up: labels join by name (§6.2), and the control has no model to ask.
    /// A label missing from here is drawn in the muted colour, which is what every label looked like
    /// before there were any.
    /// </remarks>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyDictionary<string, Color> LabelColours { get; set; } =
        new Dictionary<string, Color>(StringComparer.Ordinal);

    /// <summary>
    /// The dot in front of a row's project, or null when there is none to draw.
    /// </summary>
    /// <remarks>
    /// Nothing on a selected row: the accent is behind it, and a colour chosen to read against the
    /// panel has made no promise about that. A task in no project has no colour either — the row
    /// carries one only when it found the project — so that answers itself.
    /// </remarks>
    internal static Color? ProjectDot(TaskRow row, bool selected)
        => selected || row.ProjectColour is not { } colour
            ? null
            : Color.FromArgb(colour.R, colour.G, colour.B);

    /// <summary>The project a task is in, behind the dot Todoist gives that project.</summary>
    private void DrawProject(Graphics g, Rectangle bounds, TaskRow row, bool selected, Color muted)
    {
        var text = Inset(bounds);

        if (ProjectDot(row, selected) is { } dot)
            DrawDot(g, ref text, dot);

        TextRenderer.DrawText(g, row.Project, Font, text, muted, Flags);
    }

    /// <summary>
    /// The labels of a row, each with the colour it is written in.
    /// </summary>
    /// <remarks>
    /// A selected row comes back as one run in the one colour: the accent behind it is what the row
    /// is saying, and five colours over it say less than none. A label the window hasn't been told
    /// the colour of — one just made, before the sync describing it — reads as it always did.
    /// </remarks>
    internal IReadOnlyList<(string Text, Color Colour)> LabelRuns(TaskRow row, bool selected, Color muted)
    {
        if (selected || row.Labels.Count == 0)
            return LabelsOf(row) is { Length: > 0 } all ? [(all, muted)] : [];

        return row.Labels
            .Select(l => ("@" + l, LabelColours.TryGetValue(l, out var found) ? found : muted))
            .ToList();
    }

    /// <summary>Writes the labels along the column, one after another in their own colours.</summary>
    private void DrawLabels(Graphics g, Rectangle bounds, TaskRow row, bool selected, Color muted)
    {
        var text = Inset(bounds);

        foreach (var (written, colour) in LabelRuns(row, selected, muted))
        {
            if (text.Width <= 0)
                return;

            TextRenderer.DrawText(g, written, Font, text, colour, Flags);

            // Measured with the space that follows it, which is what puts the next one along.
            var width = TextRenderer.MeasureText(g, written + " ", Font, text.Size, Flags).Width;
            text.X += width;
            text.Width -= width;
        }
    }

    /// <summary>Draws a colour's dot at the left of <paramref name="bounds"/>, and takes its room.</summary>
    private void DrawDot(Graphics g, ref Rectangle bounds, Color colour)
    {
        var size = Math.Min(8, bounds.Height - 8);
        if (size <= 0)
            return;

        Dots.Fill(g, new Rectangle(bounds.X, bounds.Y + ((bounds.Height - size) / 2), size, size), colour);

        var taken = size + (TextInset / 2);
        bounds.X += taken;
        bounds.Width = Math.Max(0, bounds.Width - taken);
    }

    private static void DrawPriority(Graphics g, Rectangle bounds, Priority priority)
    {
        if (priority == Priority.P4)
            return;

        var colour = Theme.ForPriority(priority);

        var size = Math.Min(9, bounds.Height - 8);

        Dots.Fill(
            g,
            new Rectangle(
                bounds.X + ((bounds.Width - size) / 2),
                bounds.Y + ((bounds.Height - size) / 2),
                size,
                size),
            colour);
    }

    private static TextFormatFlags Flags
        => TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    private static Rectangle Inset(Rectangle bounds)
        => new(bounds.X + TextInset, bounds.Y, Math.Max(0, bounds.Width - (TextInset * 2)), bounds.Height);
}
