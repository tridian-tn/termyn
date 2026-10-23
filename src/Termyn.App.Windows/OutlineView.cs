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
        AllowDrop = true;

        _tipDelay.Tick += RaiseTip;

        // Each header carries the column it stands for, so a click has something to name without a
        // second table of indices to keep in step with this one.
        Column(Strings.ColumnTask, TaskColumn.Content);
        Column("!", TaskColumn.Priority, HorizontalAlignment.Center);
        Column(Strings.ColumnProject, TaskColumn.Project);
        Column(Strings.ColumnDue, TaskColumn.Due);
        Column(Strings.ColumnDeadline, TaskColumn.Deadline);
        Column(Strings.ColumnLabels, TaskColumn.Labels);

        void Column(string heading, TaskColumn column, HorizontalAlignment align = HorizontalAlignment.Left)
            => Columns.Add(heading, StartupLayout.Scaled(StartupLayout.ColumnWidth(column), DeviceDpi), align).Tag = column;
    }

    /// <summary>How wide each column is, by the column it stands for.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyDictionary<TaskColumn, int> ColumnWidths
        => Columns.Cast<ColumnHeader>()
            .Where(c => c.Tag is TaskColumn)
            .ToDictionary(c => (TaskColumn)c.Tag!, c => c.Width);

    /// <summary>
    /// Sizes the columns for a window that has just opened: as they were saved, or to fit.
    /// </summary>
    /// <remarks>
    /// Once the list is on its screen and laid out. Before that it only knows the scale of the screen
    /// the app started on, and on a second screen scaled differently that's the wrong one.
    ///
    /// A column with no saved width takes its default. When none of them had one, the task column
    /// then takes whatever the others leave of the outline, so every column shows without scrolling
    /// sideways. A scroll bar is allowed for, showing or not: this runs before the rows are in, and a
    /// real account soon has more than fit.
    /// </remarks>
    /// <param name="saved">The widths saved last time, by column</param>
    public void LayColumns(IReadOnlyDictionary<TaskColumn, int> saved)
    {
        var headers = Columns.Cast<ColumnHeader>().Where(c => c.Tag is TaskColumn).ToList();

        foreach (var header in headers)
        {
            var column = (TaskColumn)header.Tag!;
            header.Width = saved.TryGetValue(column, out var width)
                ? width
                : StartupLayout.Scaled(StartupLayout.ColumnWidth(column), DeviceDpi);
        }

        // Asked of the outline's own columns, so a width saved for one it no longer has doesn't
        // count as the user having sized it.
        if (headers.Any(h => saved.ContainsKey((TaskColumn)h.Tag!)))
            return;

        var others = headers.Where(h => h.Tag is not TaskColumn.Content).Sum(h => h.Width);
        var room = ClientSize.Width - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);

        foreach (var header in headers.Where(h => h.Tag is TaskColumn.Content))
            header.Width = StartupLayout.TaskColumnWidth(room, others, DeviceDpi);
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
            _tipDelay.Dispose();
            _tip.Dispose();
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

            // The same goes for a drag under way: the mark and the last answer about it name rows
            // by index, and the next move of the pointer works both out again from the new rows.
            _mark = null;
            _asked = null;

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

            // A tip names the priority of whatever task was under the pointer, and a sync can put
            // another one there, or change this one's, without the pointer moving at all.
            if (_tipOn is not null && IsHandleCreated)
                AimTip(PointToClient(Cursor.Position));

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
        if (index >= 0 && index < _rows.Count && _rows[index].IsHeading && OutlineSelection.PastHeading(_rows, index, _lastOnTask) is { } landed)
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

    /// <summary>
    /// The row a task is on, by the name it was noted under or the one it goes by now.
    /// </summary>
    /// <remarks>
    /// A task added a moment ago is renamed when the sync learns what the server calls it, and the
    /// rows are rebuilt under the new name. Something noted beforehand — the task under a press —
    /// would otherwise find nothing, and the drag it starts would quietly not happen.
    /// </remarks>
    /// <param name="id">The task, by whatever name it was noted under</param>
    /// <returns>Its row, or -1 when it's under neither name</returns>
    internal int RowOf(string id)
    {
        var index = IndexOf(id);

        return index < 0 && Renamed?.Invoke(id) is { } now && now != id ? IndexOf(now) : index;
    }

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

    /// <summary>
    /// Answers a press on a row's box or expander, which asks for that one thing and not the row.
    /// </summary>
    /// <remarks>
    /// The selection stays where the user put it, which is what every list does and what stops
    /// ticking a task off, or glancing at what's under it, from moving them away from what they
    /// were reading.
    /// </remarks>
    /// <param name="at">Where the button went down, in the list's own coordinates</param>
    /// <param name="second">
    /// Whether this is the second press of a double-click, which only counts on the same box or
    /// expander as the first
    /// </param>
    /// <returns>True when the press was one of those, and the list shouldn't see it</returns>
    private bool PressedBoxOrExpander(Point at, bool second)
    {
        var box = CheckboxAt(at);
        var expander = box is null ? ExpanderAt(at) : null;

        if (box is null && expander is null)
        {
            _pressed = null;
            return false;
        }

        var pressed = box is not null ? $"box:{box}" : $"expander:{expander}";

        // The first press can change the rows under the pointer — a sync lands, or a heading goes
        // with the last task under it — and the second then arrives over a different task. Taken,
        // that would tick off something nobody pointed at.
        if (second && pressed != _pressed)
            return true;

        _pressed = pressed;
        Focus();

        if (box is not null)
            ToggleRequested?.Invoke(box);
        else
            CollapseRequested?.Invoke(expander!, !IsCollapsed(expander!));

        return true;
    }

    /// <summary>
    /// The box or expander the last press landed on, so the second press of a double-click can be
    /// held to the same one.
    /// </summary>
    private string? _pressed;

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

        return InTaskColumn(index, point) && new Rectangle(bounds.X, bounds.Y, CheckWidth, bounds.Height).Contains(point) ? row.Id : null;
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

        return InTaskColumn(index, point) && Expander(bounds).Contains(point) ? row.Id : null;
    }

    /// <summary>
    /// Whether a point on a row is inside the Task column, which is where the box and the expander
    /// are drawn.
    /// </summary>
    /// <remarks>
    /// A column dragged narrower than a deep sub-task's indent draws neither — they're clipped to
    /// it — so the room they'd have had belongs to the column beside it, and a click there isn't
    /// one on a box that can't be seen.
    /// </remarks>
    /// <param name="index">The row</param>
    /// <param name="point">Where the pointer is, in the list's own coordinates</param>
    /// <returns>True when the point is left of the Task column's right edge</returns>
    private bool InTaskColumn(int index, Point point)
        => point.X < GetItemRect(index, ItemBoundsPortion.Entire).X + Columns[0].Width;

    /// <summary>
    /// Says what a priority dot means.
    /// </summary>
    /// <remarks>
    /// Shown by hand rather than handed the list with SetToolTip. That makes the whole list one
    /// tip as far as Windows is concerned, and Windows shows a tip once per visit: moving on to
    /// another row's dot left the first row's words up, and a click — which is how a row gets
    /// selected — put it away until the pointer left the list altogether. The delays are still
    /// the toolkit's own, but see <see cref="FirstPause"/>.
    /// </remarks>
    private readonly ToolTip _tip = new();

    /// <summary>
    /// How long the pointer rests on a dot before the first tip comes up, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Half as long again as the toolkit's own pause, which was a touch quick in use. Moving on
    /// from one dot to the next still takes the toolkit's short pause.
    /// </remarks>
    internal int FirstPause => _tip.InitialDelay * 3 / 2;

    /// <summary>
    /// Whether resting the pointer on a priority dot says what it means. Switching it off takes
    /// down a tip that's up.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool PriorityTips
    {
        get => _priorityTips;
        set
        {
            _priorityTips = value;
            if (!value)
                DropTip();
        }
    }

    private bool _priorityTips = true;

    /// <summary>Counts down the pause before the tip comes up.</summary>
    private readonly System.Windows.Forms.Timer _tipDelay = new();

    /// <summary>The dot the pointer is on, by task and what it says, or null when it's on none.</summary>
    private (string Id, string Text)? _tipOn;

    /// <summary>Where the pointer was last seen on that dot, which is where the tip comes up.</summary>
    private Point _tipAt;

    /// <summary>Whether the tip is up, which makes the next dot's pause the shorter one.</summary>
    private bool _tipUp;

    /// <summary>What the pointer's on, for a test to read.</summary>
    internal string? TipAimedAt => _tipOn?.Text;

    /// <summary>What the tip is showing, or null when it's down, for a test to read.</summary>
    internal string? TipShowing => _tipUp ? _tipOn?.Text : null;

    /// <summary>
    /// Has the pointer come to a point, as a move there would.
    /// </summary>
    /// <remarks>
    /// For a test, which can't send the move itself: Windows answers a move posted to a window the
    /// real pointer isn't over by saying it's left again straight away.
    /// </remarks>
    /// <param name="point">Where the pointer is, in the list's own coordinates</param>
    internal void PointerAt(Point point) => AimTip(point);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        AimTip(e.Location);
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        DropTip();
        base.OnMouseLeave(e);
    }

    /// <summary>
    /// The wheel moves the rows under a pointer that stays put, so the dot it was on has gone.
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        DropTip();
        base.OnMouseWheel(e);
    }

    /// <summary>
    /// Points the tip at whatever dot's under a point, starting the pause when it's a new one.
    /// </summary>
    /// <remarks>
    /// Another row's dot counts as new even when it says the same, so the tip moves with the
    /// pointer rather than staying by a row it's left. Coming off one dot straight onto another
    /// takes the short pause, the way moving between any two tips does.
    /// </remarks>
    /// <param name="point">Where the pointer is, in the list's own coordinates</param>
    private void AimTip(Point point)
    {
        var on = _priorityTips ? TipAt(point) : null;
        _tipAt = point;

        if (on == _tipOn)
            return;

        var wasUp = _tipUp;
        DropTip();

        _tipOn = on;
        if (on is null)
            return;

        _tipDelay.Interval = wasUp ? _tip.ReshowDelay : FirstPause;
        _tipDelay.Start();
    }

    /// <summary>Puts the tip up, once the pointer's rested on a dot for long enough.</summary>
    private void RaiseTip(object? sender, EventArgs e)
    {
        _tipDelay.Stop();
        if (_tipOn is not { } on)
            return;

        // Below the pointer rather than on it, where a tip Windows placed itself would be.
        _tip.Show(on.Text, this, new Point(_tipAt.X, _tipAt.Y + Cursor.Size.Height), _tip.AutoPopDelay);
        _tipUp = true;
    }

    /// <summary>Takes the tip down and forgets the dot it was for.</summary>
    private void DropTip()
    {
        _tipDelay.Stop();
        _tipOn = null;

        if (!_tipUp)
            return;

        _tip.Hide(this);
        _tipUp = false;
    }

    /// <summary>
    /// What the priority under a point says, or null where there's no dot.
    /// </summary>
    /// <remarks>
    /// The whole cell answers rather than the dot drawn in it. The dot's nine pixels across, and
    /// a target that small is one people miss; the column's only there to hold it.
    /// </remarks>
    /// <param name="point">Where the pointer is, in the list's own coordinates</param>
    /// <returns>The priority written out, or null when the point isn't on a dot's cell</returns>
    internal string? PriorityTipAt(Point point) => TipAt(point)?.Text;

    /// <summary>
    /// The dot under a point, by the task it's on and what it says.
    /// </summary>
    /// <param name="point">Where the pointer is, in the list's own coordinates</param>
    /// <returns>The task's id and the priority written out, or null when there's no dot there</returns>
    private (string Id, string Text)? TipAt(Point point)
    {
        if (HitTest(point).Item?.Index is not { } index || index < 0 || index >= _rows.Count)
            return null;

        if (ColumnAt(index, point) is not { } column || OutlineCells.PaintOf(column) != CellPaint.Priority)
            return null;

        return OutlineCells.PriorityTip(_rows[index]) is { } text ? (_rows[index].Id, text) : null;
    }

    /// <summary>
    /// Which column a point on a row is in, measured from the row's own left edge so a list
    /// scrolled sideways still answers for what's under the pointer.
    /// </summary>
    /// <param name="index">The row</param>
    /// <param name="point">Where the pointer is, in the list's own coordinates</param>
    /// <returns>The column, or null past the last of them</returns>
    private TaskColumn? ColumnAt(int index, Point point)
    {
        var left = GetItemRect(index, ItemBoundsPortion.Entire).X;

        foreach (ColumnHeader header in Columns)
        {
            if (point.X < left + header.Width)
                return header.Tag is TaskColumn column ? column : null;

            left += header.Width;
        }

        return null;
    }

    /// <summary>Whether the row for a task is currently collapsed, as the last rows said.</summary>
    private bool IsCollapsed(string id)
    {
        foreach (var row in _rows)
            if (row.Id == id)
                return row.Collapsed;

        return false;
    }

    // ---- Dragging ------------------------------------------------------------------------------

    /// <summary>What a drag out of the outline carries, under a name of its own.</summary>
    private const string DragFormat = "Termyn.Task";

    /// <summary>How thick the line showing where a drop would land is drawn.</summary>
    private const int MarkThickness = 2;

    /// <summary>
    /// How long to wait between scrolling a row at a time while a drag is held near the top or the
    /// bottom of the list.
    /// </summary>
    private const int ScrollEvery = 60;

    /// <summary>
    /// Asks whether a dragged task could be put down where the pointer is. Null turns every drop away.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string, DropTarget, bool>? CanDrop { get; set; }

    /// <summary>Raised when a dragged task is let go somewhere it can go, with the task and where.</summary>
    public event Action<string, DropTarget>? DropRequested;

    /// <summary>The task being dragged, or null when no drag out of this list is under way.</summary>
    private string? _dragging;

    /// <summary>
    /// The task being dragged out of the list, for somewhere else in the window to take, or null
    /// when there's no drag under way.
    /// </summary>
    public string? Dragging => _dragging;

    /// <summary>The task under the last press of the left button, which is the one a drag takes.</summary>
    private string? _pressedTask;

    /// <summary>Where the mark is drawn now, or null when there's none.</summary>
    private DropTarget? _mark;

    /// <summary>
    /// The drop last asked about, for which task, and what the answer was, since the pointer sits
    /// over the same one for many moves in a row and the question walks the account to answer.
    /// </summary>
    private DropTarget? _asked;

    private string? _askedFor;

    private bool _allowed;

    /// <summary>Where the drag was let go, kept until the drag has finished to act on.</summary>
    private DropTarget? _dropped;

    /// <summary>When the list last scrolled for a drag, so holding still near an edge doesn't race.</summary>
    private long _scrolled;

    /// <summary>
    /// Starts a drag from a task's row.
    /// </summary>
    /// <remarks>
    /// From any view, since the sidebar takes a task wherever it was dragged from. Where in this
    /// list it can be put down is the presenter's to say, drop by drop, and in some views that's
    /// nowhere at all.
    ///
    /// A finished task isn't dragged, since its place among the others stopped meaning anything
    /// once it was done; nor is a day's heading, which isn't a task.
    ///
    /// The task is the one noted when the button went down, found again by name, rather than
    /// whatever's at the index the list hands over: the rows can have been rebuilt in between, and
    /// that index can name the task that's moved up into the pressed one's place.
    ///
    /// The drop is acted on once the drag is over rather than from inside it. A drop rebuilds the
    /// rows, and doing that inside the drag's own loop would replace the list under the code that's
    /// still busy finishing the drag with it.
    /// </remarks>
    protected override void OnItemDrag(ItemDragEventArgs e)
    {
        base.OnItemDrag(e);

        if (e.Button != MouseButtons.Left || _pressedTask is not { } pressed || RowOf(pressed) is not (>= 0 and var index))
            return;

        var row = _rows[index];
        if (row.IsHeading || row.Completed)
            return;

        _dragging = row.Id;
        _dropped = null;

        try
        {
            DoDragDrop(new DataObject(DragFormat, row.Id), DragDropEffects.Move);
        }
        finally
        {
            _dragging = null;
            _asked = null;
            Mark(null);
        }

        if (_dropped is { } target)
        {
            _dropped = null;
            DropRequested?.Invoke(row.Id, target);
        }
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        Follow(drgevent);
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        Follow(drgevent);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        Mark(null);
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        _dropped = _dragging is { } id ? DropAt(id, PointToClient(new Point(drgevent.X, drgevent.Y))) : null;
        Mark(null);
    }

    /// <summary>
    /// Keeps up with the pointer during a drag: scrolls when it's held at an edge, marks where a
    /// drop would land, and tells the pointer whether it can land there at all.
    /// </summary>
    /// <param name="e">The drag as it stands</param>
    private void Follow(DragEventArgs e)
    {
        var client = PointToClient(new Point(e.X, e.Y));

        // Only a drag that started in this list. Anything else dragged over it — a file from
        // Explorer, a task from a second window — finds nowhere to land.
        if (_dragging is not { } id)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        // Scrolled first, so what's marked is whatever has just come under the pointer.
        ScrollNear(client);

        var target = DropAt(id, client);
        e.Effect = target is null ? DragDropEffects.None : DragDropEffects.Move;
        Mark(target);
    }

    /// <summary>
    /// The drop a point asks for, or null when there's nothing it could do there.
    /// </summary>
    /// <param name="id">The task being dragged</param>
    /// <param name="client">Where the pointer is, in the list's own coordinates</param>
    /// <returns>The drop, or null when it'd be turned away</returns>
    internal DropTarget? DropAt(string id, Point client)
    {
        if (_rows.Count == 0)
            return null;

        var index = RowAt(client.Y);
        DropTarget? target;

        if (index >= 0)
        {
            var bounds = GetItemRect(index, ItemBoundsPortion.Entire);
            target = OutlineDrop.Over(_rows, index, OutlineDrop.ZoneAt(client.Y - bounds.Top, bounds.Height));
        }
        else
        {
            target = client.Y >= GetItemRect(_rows.Count - 1, ItemBoundsPortion.Entire).Bottom ? OutlineDrop.Below(_rows) : null;
        }

        if (target is null)
            return null;

        if (target != _asked || id != _askedFor)
        {
            _asked = target;
            _askedFor = id;
            _allowed = CanDrop?.Invoke(id, target) == true;
        }

        return _allowed ? target : null;
    }

    /// <summary>
    /// The row at a height in the list, wherever across it the pointer is.
    /// </summary>
    /// <remarks>
    /// Asked just inside the list's left edge, on the same line. A drop goes by which row it's over
    /// and not by which column, and past the last column the list says there's no row there at all.
    /// The list's edge rather than the first column's, since scrolled sideways that column starts
    /// off to the left, and the list won't say what's under a point outside itself.
    /// </remarks>
    /// <param name="y">How far down the list, in its own coordinates</param>
    /// <returns>The row's index, or -1 when there's none at that height</returns>
    private int RowAt(int y)
        => HitTest(new Point(1, y)).Item?.Index is { } index && index >= 0 && index < _rows.Count ? index : -1;

    /// <summary>
    /// Scrolls a row at a time while a drag is held within a row of the top or the bottom, so a
    /// task can be taken further than the list shows at once.
    /// </summary>
    /// <param name="client">Where the pointer is, in the list's own coordinates</param>
    private void ScrollNear(Point client)
    {
        if (_rows.Count == 0 || TopItem is not { } top)
            return;

        var first = GetItemRect(top.Index, ItemBoundsPortion.Entire);
        var height = first.Height;
        if (height <= 0)
            return;

        var shown = Math.Max(1, (ClientSize.Height - first.Top) / height);
        var to = client.Y < first.Top + height ? top.Index - 1
            : client.Y > ClientSize.Height - height ? top.Index + shown
            : -1;

        if (to < 0 || to >= _rows.Count || Environment.TickCount64 - _scrolled < ScrollEvery)
            return;

        _scrolled = Environment.TickCount64;
        EnsureVisible(to);
    }

    /// <summary>Moves the mark to show where a drop would land, or takes it away.</summary>
    /// <param name="target">Where to mark, or null for nowhere</param>
    private void Mark(DropTarget? target)
    {
        if (target == _mark)
            return;

        Redraw(_mark);
        _mark = target;
        Redraw(_mark);

        void Redraw(DropTarget? marked)
        {
            if (marked is { Row: var row } && row >= 0 && row < _rows.Count)
                Invalidate(GetItemRect(row, ItemBoundsPortion.Entire));
        }
    }

    /// <summary>
    /// Draws a cell's share of the mark showing where a drop would land.
    /// </summary>
    /// <remarks>
    /// A line along the row's top edge for before and its bottom edge for after, and a box round it
    /// for onto. It starts at the indent of the level the task would land at, so a drop between a
    /// sub-task and the next top-level task shows which of the two it would be alongside. The list
    /// hands a row out a cell at a time, so each cell draws the part of the mark that's in it.
    /// </remarks>
    /// <param name="g">What to draw on</param>
    /// <param name="cell">The cell being drawn</param>
    /// <param name="index">The row it's in</param>
    /// <param name="column">Which column it is</param>
    private void DrawDropMark(Graphics g, Rectangle cell, int index, TaskColumn column)
    {
        if (_mark is not { } mark || mark.Row != index)
            return;

        var left = column == TaskColumn.Content ? Math.Min(cell.Right, cell.Left + (mark.Depth * IndentWidth)) : cell.Left;
        var width = cell.Right - left;

        using var brush = new SolidBrush(Theme.Accent);

        if (mark.Zone != DropZone.After)
            g.FillRectangle(brush, left, cell.Top, width, MarkThickness);

        if (mark.Zone != DropZone.Before)
            g.FillRectangle(brush, left, cell.Bottom - MarkThickness, width, MarkThickness);

        if (mark.Zone == DropZone.Onto && column == TaskColumn.Content)
            g.FillRectangle(brush, left, cell.Top, MarkThickness, cell.Height);
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
            cells[i] = Columns[i].Tag is TaskColumn column ? OutlineCells.CellOf(row, column) : string.Empty;

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

        // A press puts the tip away, as it does anywhere. Here rather than in OnMouseDown, which
        // a press on a box, an expander or a heading never reaches. The next move brings it back
        // after the pause, so a row that's just been selected still says what its dot means.
        if (m.Msg is WmLeftDown or WmLeftDouble or WmRightDown)
            DropTip();

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
        if (m.Msg is WmLeftDown or WmLeftDouble or WmRightDown && IsHeadingAt(PointOf(m.LParam)))
            return;

        // Answered here rather than in OnMouseDown, because returning from that doesn't stop
        // anything: the list raises it first and selects the row under the press afterwards. A
        // second press that arrives as a double-click is a second press on the box, the way it is
        // on any checkbox, and not a request to open the task.
        if (m.Msg is WmLeftDown or WmLeftDouble && PressedBoxOrExpander(PointOf(m.LParam), second: m.Msg == WmLeftDouble))
            return;

        // The task under a press, for a drag that starts from it. The list names the row by the
        // index it had when the button went down, and the rows can be rebuilt before the pointer
        // has moved far enough to count as a drag — a sync landing, or a ticked-off task leaving.
        if (m.Msg == WmLeftDown)
            _pressedTask = HitTest(PointOf(m.LParam)).Item?.Index is { } pressed && pressed >= 0 && pressed < _rows.Count ? _rows[pressed].Id : null;

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
    /// Where a button went down, from the position a mouse message packs into its lParam.
    /// </summary>
    /// <remarks>
    /// Client coordinates, unlike the context menu's, which the shell sends in screen ones.
    /// </remarks>
    /// <param name="lParam">Where the button went down, as the message carries it</param>
    /// <returns>The point, in the list's own coordinates</returns>
    private static Point PointOf(nint lParam) => new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

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
        // read alongside the work that is still outstanding. A recurring one waiting to hear its
        // next date looks the same, so ticking it off visibly does something.
        var done = OutlineCells.DrawnDone(row);
        var text = selected ? Theme.OnAccent : done ? Theme.Muted : Theme.Text;
        var muted = selected ? Theme.OnAccent : Theme.Muted;
        var font = done ? Struck : Font;

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

        switch (OutlineCells.PaintOf(column))
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

                DrawCheck(e.Graphics, Checkbox(bounds), done, selected ? Theme.OnAccent : Theme.Muted);

                bounds.X += CheckWidth;
                bounds.Width -= CheckWidth;
                TextRenderer.DrawText(e.Graphics, OutlineCells.CellOf(row, column), font, Inset(bounds), text, Flags);
                break;

            // The same face the task's own column uses, so a finished task's dates are struck
            // through with its name rather than left reading as though they still stood.
            default:
                TextRenderer.DrawText(e.Graphics, OutlineCells.CellOf(row, column), font, Inset(e.Bounds), muted, Flags);
                break;
        }

        DrawDropMark(e.Graphics, e.Bounds, e.ItemIndex, column);
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
        new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The project a task is in, behind the dot Todoist gives that project.</summary>
    private void DrawProject(Graphics g, Rectangle bounds, TaskRow row, bool selected, Color muted)
    {
        var text = Inset(bounds);

        if (OutlineCells.ProjectDot(row, selected) is { } dot)
            DrawDot(g, ref text, Theme.ToColor(dot));

        TextRenderer.DrawText(g, row.Project, Font, text, muted, Flags);
    }

    /// <summary>
    /// The labels of a row as this list writes them, each in its colour.
    /// </summary>
    /// <remarks>
    /// Which colour each gets is Presentation's; this is only where the list's own colours go in.
    /// Kept apart from the drawing so a test can check the list uses the colours it's given.
    /// </remarks>
    /// <param name="row">The task the cell belongs to</param>
    /// <param name="selected">Whether the row is drawn selected</param>
    /// <param name="muted">The colour for a label with none of its own</param>
    /// <returns>Each run of text with the colour to write it in</returns>
    internal IEnumerable<(string Text, Color Colour)> LabelRuns(TaskRow row, bool selected, Color muted)
        => OutlineCells.LabelRuns(row, selected, LabelColours).Select(run => (run.Text, run.Colour ?? muted));

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
