using System.Globalization;
using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The sidebar: the smart views, then Favourites, Projects, Labels and Filters, as a source list.
/// </summary>
/// <remarks>
/// Built from the presenter's flat list, nested by depth the way the Windows tree is: a row's parent
/// is the last row seen one level up. The headings are group rows, so they take the system's
/// heading style and the Show/Hide that appears over them on hover.
/// </remarks>
internal sealed class SidebarController : NSViewController
{
    private readonly Action<string> _select;
    private readonly NSOutlineView _outline = new();
    private readonly HashSet<string> _collapsed = [];

    private IReadOnlyList<SidebarNode> _shown = [];
    private List<Item> _roots = [];

    /// <summary>
    /// Set while the outline is being filled in from the presenter, so the selection and expansion
    /// that does aren't taken as the user's.
    /// </summary>
    private bool _rendering;

    public SidebarController(Action<string> select) => _select = select;

    public override void LoadView()
    {
        var column = new NSTableColumn("Name");
        _outline.AddColumn(column);
        _outline.OutlineTableColumn = column;
        _outline.HeaderView = null;
        _outline.Style = NSTableViewStyle.SourceList;

        // The default, which follows the sidebar icon size chosen in System Settings.
        _outline.RowSizeStyle = NSTableViewRowSizeStyle.Default;
        _outline.DataSource = new Source(this);
        _outline.Delegate = new Delegate(this);

        View = new NSScrollView
        {
            DocumentView = _outline,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
        };
    }

    /// <summary>Shows the presenter's sidebar, rebuilding only when it changed.</summary>
    /// <param name="nodes">The rows, flat, nested by their depth</param>
    /// <param name="selectedKey">The key of the row the presenter has selected</param>
    public void Render(IReadOnlyList<SidebarNode> nodes, string selectedKey)
    {
        _rendering = true;
        try
        {
            if (!nodes.SequenceEqual(_shown))
            {
                _shown = nodes;
                _roots = Nest(nodes);
                _outline.ReloadData();
                Expand(_roots);
            }

            Select(selectedKey);
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>A row's parent is the last row seen one level up, as the Windows tree reads it.</summary>
    private static List<Item> Nest(IReadOnlyList<SidebarNode> nodes)
    {
        var roots = new List<Item>();
        var last = new List<Item>();

        foreach (var node in nodes)
        {
            var item = new Item(node);
            if (node.Depth > 0 && node.Depth <= last.Count)
                last[node.Depth - 1].Children.Add(item);
            else
                roots.Add(item);

            var depth = Math.Min(node.Depth, last.Count);
            last.RemoveRange(depth, last.Count - depth);
            last.Add(item);
        }

        return roots;
    }

    /// <summary>Opens everything the user hasn't closed, so a new project arrives open.</summary>
    private void Expand(IEnumerable<Item> items)
    {
        foreach (var item in items.Where(i => i.Children.Count > 0 && !_collapsed.Contains(i.Node.Key)))
        {
            _outline.ExpandItem(item);
            Expand(item.Children);
        }
    }

    private void Select(string key)
    {
        var row = Find(_roots, key) is { } item ? _outline.RowForItem(item) : -1;
        if (row < 0)
        {
            _outline.DeselectAll(this);
            return;
        }

        if (row != _outline.SelectedRow)
        {
            _outline.SelectRow(row, byExtendingSelection: false);
            _outline.ScrollRowToVisible(row);
        }
    }

    private static Item? Find(IEnumerable<Item> items, string key)
    {
        foreach (var item in items)
        {
            if (item.Node.Key == key)
                return item;

            if (Find(item.Children, key) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>The symbol for a row, and the colour it takes when it has one of its own.</summary>
    private static (string Symbol, NSColor? Tint) Look(SidebarNode node) => node.Kind switch
    {
        SidebarKind.SmartView => (node.View switch
        {
            SmartView.Today => "calendar",
            SmartView.Upcoming => "calendar.badge.clock",
            _ => "tray",
        }, null),
        SidebarKind.Project => ("circle.fill", Tint(node)),
        SidebarKind.Section => ("rectangle.split.3x1", NSColor.SecondaryLabel),
        SidebarKind.Label => ("tag", Tint(node)),
        _ => ("line.3.horizontal.decrease.circle", Tint(node)),
    };

    private static NSColor? Tint(SidebarNode node) => node.Colour is { } colour ? Palette.Of(colour) : null;

    /// <summary>One row, wrapped as the object AppKit's outline asks for.</summary>
    private sealed class Item(SidebarNode node) : NSObject
    {
        public SidebarNode Node { get; } = node;

        public List<Item> Children { get; } = [];
    }

    private sealed class Source(SidebarController owner) : NSOutlineViewDataSource
    {
        public override nint GetChildrenCount(NSOutlineView outlineView, NSObject? item)
            => item is Item parent ? parent.Children.Count : owner._roots.Count;

        public override NSObject GetChild(NSOutlineView outlineView, nint childIndex, NSObject? item)
            => item is Item parent ? parent.Children[(int)childIndex] : owner._roots[(int)childIndex];

        public override bool ItemExpandable(NSOutlineView outlineView, NSObject item)
            => ((Item)item).Children.Count > 0;
    }

    private sealed class Delegate(SidebarController owner) : NSOutlineViewDelegate
    {
        private const string RowId = "Row";
        private const string HeadingId = "Heading";

        public override bool IsGroupItem(NSOutlineView outlineView, NSObject item)
            => ((Item)item).Node.Kind == SidebarKind.Header;

        public override bool ShouldSelectItem(NSOutlineView outlineView, NSObject item)
            => ((Item)item).Node.Kind != SidebarKind.Header;

        public override NSView GetView(NSOutlineView outlineView, NSTableColumn? tableColumn, NSObject item)
        {
            var node = ((Item)item).Node;

            if (node.Kind == SidebarKind.Header)
            {
                var heading = outlineView.MakeView(HeadingId, owner) as NSTableCellView ?? Cell.Heading(HeadingId);
                heading.TextField!.StringValue = node.Label;
                return heading;
            }

            var cell = outlineView.MakeView(RowId, owner) as Cell ?? new Cell(RowId);
            cell.Show(node);
            return cell;
        }

        public override void SelectionDidChange(NSNotification notification)
        {
            if (owner._rendering || owner._outline.ItemAtRow(owner._outline.SelectedRow) is not Item item)
                return;

            owner._select(item.Node.Key);
        }

        public override void ItemDidCollapse(NSNotification notification) => Track(notification, collapsed: true);

        public override void ItemDidExpand(NSNotification notification) => Track(notification, collapsed: false);

        private void Track(NSNotification notification, bool collapsed)
        {
            if (owner._rendering || notification.UserInfo?["NSObject"] is not Item item)
                return;

            if (collapsed)
                owner._collapsed.Add(item.Node.Key);
            else
                owner._collapsed.Remove(item.Node.Key);
        }
    }

    /// <summary>A row: its symbol, its name and, on the right, how many tasks it holds.</summary>
    private sealed class Cell : NSTableCellView
    {
        private readonly NSTextField _count;

        public Cell(string identifier)
        {
            Identifier = identifier;

            var image = new NSImageView { TranslatesAutoresizingMaskIntoConstraints = false };
            var name = Label();
            name.LineBreakMode = NSLineBreakMode.TruncatingTail;
            name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

            _count = Label();
            _count.TextColor = NSColor.SecondaryLabel;
            _count.Alignment = NSTextAlignment.Right;
            _count.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);

            AddSubview(image);
            AddSubview(name);
            AddSubview(_count);
            ImageView = image;
            TextField = name;

            NSLayoutConstraint.ActivateConstraints(
            [
                image.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 2),
                image.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
                image.WidthAnchor.ConstraintEqualTo(18),
                name.LeadingAnchor.ConstraintEqualTo(image.TrailingAnchor, 6),
                name.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
                _count.LeadingAnchor.ConstraintGreaterThanOrEqualTo(name.TrailingAnchor, 6),
                _count.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -4),
                _count.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            ]);
        }

        public static NSTableCellView Heading(string identifier)
        {
            var cell = new NSTableCellView { Identifier = identifier };
            var text = Label();
            cell.AddSubview(text);
            cell.TextField = text;

            NSLayoutConstraint.ActivateConstraints(
            [
                text.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 2),
                text.TrailingAnchor.ConstraintLessThanOrEqualTo(cell.TrailingAnchor, -2),
                text.CenterYAnchor.ConstraintEqualTo(cell.CenterYAnchor),
            ]);
            return cell;
        }

        public void Show(SidebarNode node)
        {
            var (symbol, tint) = Look(node);
            ImageView!.Image = NSImage.GetSystemSymbol(symbol, null);
            ImageView.ContentTintColor = tint;
            TextField!.StringValue = node.Label;
            _count.StringValue = node.Count > 0 ? node.Count.ToString(CultureInfo.CurrentCulture) : string.Empty;
        }

        private static NSTextField Label()
        {
            var label = NSTextField.CreateLabel(string.Empty);
            label.TranslatesAutoresizingMaskIntoConstraints = false;
            return label;
        }
    }
}
