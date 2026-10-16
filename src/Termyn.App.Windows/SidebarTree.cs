using System.ComponentModel;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The sidebar's tree, which takes two kinds of drop: a project or a section dragged among the rows
/// it sits with, and a task dragged in from the outline onto the project or section it should go to.
/// </summary>
/// <remarks>
/// Whether a drop can be made is asked of the presenter; this works out which drop the pointer is
/// asking for, marks it, and says when it's been let go.
/// </remarks>
internal sealed class SidebarTree : BufferedTreeView
{
    /// <summary>What a drag out of the sidebar carries, under a name of its own.</summary>
    private const string DragFormat = "Termyn.SidebarRow";

    /// <summary>How thick the mark showing where a drop would land is drawn.</summary>
    private const int MarkThickness = 2;

    /// <summary>
    /// How long to wait between scrolling a row at a time while a drag is held near the top or the
    /// bottom of the tree.
    /// </summary>
    private const int ScrollEvery = 60;

    public SidebarTree()
    {
        AllowDrop = true;
    }

    /// <summary>Asks whether a row can be dragged. Null leaves dragging off.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<SidebarNode, bool>? CanDrag { get; set; }

    /// <summary>
    /// Asks whether a dragged row can be put down beside another: the dragged row, the one beside,
    /// and whether it'd go after rather than before. Null turns every such drop away.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<SidebarNode, SidebarNode, bool, bool>? CanDropRow { get; set; }

    /// <summary>
    /// Asks which task is being dragged out of the outline, if one is. Null takes no tasks.
    /// </summary>
    /// <remarks>
    /// Asked of the outline rather than read off the drag, so only a drag that started in this
    /// window counts.
    /// </remarks>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string?>? DraggedTask { get; set; }

    /// <summary>Asks whether a task can be put down on a row. Null turns every task away.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string, SidebarNode, bool>? CanDropTask { get; set; }

    /// <summary>What the mark is drawn in, which the theme decides.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color MarkColour { get; set; } = SystemColors.Highlight;

    /// <summary>
    /// Raised when a dragged row is let go somewhere it can go: the row, the one it's beside, and
    /// whether it goes after that one.
    /// </summary>
    public event Action<SidebarNode, SidebarNode, bool>? RowDropRequested;

    /// <summary>Raised when a task from the outline is let go on a row that takes it.</summary>
    public event Action<string, SidebarNode>? TaskDropRequested;

    /// <summary>The row being dragged, or null when no drag out of this tree is under way.</summary>
    private SidebarNode? _dragging;

    /// <summary>Where the mark is drawn now, or null when there's none.</summary>
    private SidebarDrop? _mark;

    /// <summary>
    /// The drop last asked about, for what, and what the answer was, since the pointer sits over the
    /// same one for many moves in a row.
    /// </summary>
    private SidebarDrop? _asked;

    private string? _askedFor;

    private bool _allowed;

    /// <summary>Where a drag out of this tree was let go, kept until the drag has finished to act on.</summary>
    private SidebarDrop? _dropped;

    /// <summary>When the tree last scrolled for a drag, so holding still near an edge doesn't race.</summary>
    private long _scrolled;

    /// <summary>
    /// Starts a drag from a row, where the presenter allows one.
    /// </summary>
    /// <remarks>
    /// The drop is acted on once the drag is over rather than from inside it. A drop rebuilds the
    /// tree, and doing that inside the drag's own loop would replace the nodes under the code that's
    /// still busy finishing the drag with them.
    /// </remarks>
    protected override void OnItemDrag(ItemDragEventArgs e)
    {
        base.OnItemDrag(e);

        if (e.Button != MouseButtons.Left || e.Item is not TreeNode { Tag: SidebarNode node } || CanDrag?.Invoke(node) != true)
            return;

        _dragging = node;
        _dropped = null;

        try
        {
            DoDragDrop(new DataObject(DragFormat, node.Key), DragDropEffects.Move);
        }
        finally
        {
            _dragging = null;
            _asked = null;
            Mark(null);
        }

        if (_dropped is { } drop)
        {
            _dropped = null;
            RowDropRequested?.Invoke(node, drop.Target, drop.Zone == DropZone.After);
        }
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);

        // Each pass over the tree asks afresh. It isn't always rebuilt between two drags of the same
        // task, when a move leaves every count as it was, and the first drop can have changed what
        // the second would do.
        _asked = null;
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

    /// <summary>
    /// Takes a drop. One from this tree is kept to act on once its drag is over; one from the
    /// outline is posted, so it's acted on after the outline's drag loop has finished as well.
    /// </summary>
    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);

        var client = PointToClient(new Point(drgevent.X, drgevent.Y));
        Mark(null);

        if (_dragging is { } dragged)
        {
            _dropped = RowDropAt(dragged, client);
            return;
        }

        if (DraggedTask?.Invoke() is { } task && TaskDropAt(task, client) is { } drop)
            BeginInvoke(() => TaskDropRequested?.Invoke(task, drop.Target));
    }

    /// <summary>
    /// Keeps up with the pointer during a drag: scrolls when it's held at an edge, marks where a
    /// drop would land, and tells the pointer whether it can land there at all.
    /// </summary>
    /// <param name="e">The drag as it stands</param>
    private void Follow(DragEventArgs e)
    {
        var client = PointToClient(new Point(e.X, e.Y));
        var task = _dragging is null ? DraggedTask?.Invoke() : null;

        // Only a drag that started in this window. Anything else dragged over the tree — a file
        // from Explorer, a task from a second window — finds nowhere to land.
        if (_dragging is null && task is null)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        // Scrolled first, so what's marked is whatever has just come under the pointer.
        ScrollNear(client);

        var drop = _dragging is { } dragged ? RowDropAt(dragged, client) : TaskDropAt(task!, client);
        e.Effect = drop is null ? DragDropEffects.None : DragDropEffects.Move;
        Mark(drop);
    }

    /// <summary>
    /// Where a dragged row would be put down, or null when it'd be turned away.
    /// </summary>
    /// <remarks>
    /// By halves: the top of a row puts it before that row, and the bottom after. After a row that's
    /// open lands below everything shown under it, so that's where the mark goes — at the row's own
    /// indent, which is the level it'd land at.
    /// </remarks>
    /// <param name="dragged">The row being dragged</param>
    /// <param name="client">Where the pointer is, in the tree's own coordinates</param>
    /// <returns>The drop, or null when there's none to make there</returns>
    internal SidebarDrop? RowDropAt(SidebarNode dragged, Point client)
    {
        if (GetNodeAt(client) is not { Tag: SidebarNode beside } row)
            return null;

        var bounds = row.Bounds;
        var after = client.Y >= bounds.Top + (bounds.Height / 2);
        var drop = new SidebarDrop(beside, after ? DropZone.After : DropZone.Before, after ? LastShown(row) : row, bounds.Left);

        return Allowed(drop, dragged.Key, () => CanDropRow?.Invoke(dragged, beside, after) == true);
    }

    /// <summary>
    /// Where a task dragged in from the outline would be put down, or null when it'd be turned away.
    /// </summary>
    /// <param name="task">The task being dragged</param>
    /// <param name="client">Where the pointer is, in the tree's own coordinates</param>
    /// <returns>The drop, onto the row under the pointer, or null when that row doesn't take it</returns>
    internal SidebarDrop? TaskDropAt(string task, Point client)
    {
        if (GetNodeAt(client) is not { Tag: SidebarNode onto } row)
            return null;

        var drop = new SidebarDrop(onto, DropZone.Onto, row, row.Bounds.Left);

        return Allowed(drop, task, () => CanDropTask?.Invoke(task, onto) == true);
    }

    /// <summary>
    /// The answer for a drop, asked once while the pointer stays over it.
    /// </summary>
    /// <param name="drop">The drop the pointer is asking for</param>
    /// <param name="what">What's being dragged, since the same drop means something else for another</param>
    /// <param name="ask">Asks the presenter</param>
    /// <returns>The drop, or null when it's turned away</returns>
    private SidebarDrop? Allowed(SidebarDrop drop, string what, Func<bool> ask)
    {
        if (drop != _asked || what != _askedFor)
        {
            _asked = drop;
            _askedFor = what;
            _allowed = ask();
        }

        return _allowed ? drop : null;
    }

    /// <summary>The last row drawn under another, however deep, or that row itself when it's shut.</summary>
    private static TreeNode LastShown(TreeNode row)
    {
        var last = row;
        while (last is { IsExpanded: true, LastNode: { } child })
            last = child;

        return last;
    }

    /// <summary>
    /// Scrolls a row at a time while a drag is held within a row of the top or the bottom, so a
    /// project can be taken further than the tree shows at once.
    /// </summary>
    /// <param name="client">Where the pointer is, in the tree's own coordinates</param>
    private void ScrollNear(Point client)
    {
        if (TopNode is not { } top || ItemHeight <= 0)
            return;

        var to = client.Y < ItemHeight ? top.PrevVisibleNode
            : client.Y > ClientSize.Height - ItemHeight ? FirstBelow(top)
            : null;

        if (to is null || Environment.TickCount64 - _scrolled < ScrollEvery)
            return;

        _scrolled = Environment.TickCount64;
        to.EnsureVisible();

        // The first row past the bottom edge, found by walking down from the top one.
        TreeNode? FirstBelow(TreeNode from)
        {
            for (var node = from; node is not null; node = node.NextVisibleNode)
                if (node.Bounds.Bottom > ClientSize.Height)
                    return node;

            return null;
        }
    }

    /// <summary>Moves the mark to show where a drop would land, or takes it away.</summary>
    /// <param name="drop">Where to mark, or null for nowhere</param>
    private void Mark(SidebarDrop? drop)
    {
        if (drop == _mark)
            return;

        Redraw(_mark);
        _mark = drop;
        Redraw(_mark);

        void Redraw(SidebarDrop? marked)
        {
            if (marked?.Row is { TreeView: not null } row)
                Invalidate(new Rectangle(0, row.Bounds.Top, ClientSize.Width, row.Bounds.Height));
        }
    }

    /// <summary>
    /// Draws the row as the form asks, then the mark over it when it's on this row.
    /// </summary>
    /// <remarks>
    /// A line along the row's top edge for before and its bottom edge for after, and a box round it
    /// for a task about to be put in it. It runs from the indent of the row the drop is by to the
    /// tree's right edge.
    /// </remarks>
    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        base.OnDrawNode(e);

        if (_mark is not { } mark || e.Node != mark.Row)
            return;

        var row = e.Node.Bounds;
        var left = Math.Max(0, mark.Left);
        var width = ClientSize.Width - left;

        using var brush = new SolidBrush(MarkColour);

        if (mark.Zone != DropZone.After)
            e.Graphics.FillRectangle(brush, left, row.Top, width, MarkThickness);

        if (mark.Zone != DropZone.Before)
            e.Graphics.FillRectangle(brush, left, row.Bottom - MarkThickness, width, MarkThickness);

        if (mark.Zone == DropZone.Onto)
        {
            e.Graphics.FillRectangle(brush, left, row.Top, MarkThickness, row.Height);
            e.Graphics.FillRectangle(brush, ClientSize.Width - MarkThickness, row.Top, MarkThickness, row.Height);
        }
    }
}

/// <summary>
/// A drop worked out against the sidebar: what it'd be by, how, and where the mark goes.
/// </summary>
/// <param name="Target">The row the drop is by, or on</param>
/// <param name="Zone">Before or after it for a dragged row, onto it for a task</param>
/// <param name="Row">The row the mark is drawn on, which after an open row is the last one under it</param>
/// <param name="Left">How far in the mark starts, which is where the target row's own text starts</param>
internal sealed record SidebarDrop(SidebarNode Target, DropZone Zone, TreeNode Row, int Left);
