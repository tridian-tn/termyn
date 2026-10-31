using CoreGraphics;
using Termyn.Core.Api;
using Termyn.Core.Logging;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The main window: a sidebar of views beside the tasks in the selected one, under a unified
/// toolbar, with the view's name as the title and how the sync stands as the subtitle.
/// </summary>
/// <remarks>
/// A subtitle rather than the Windows app's status bar, which isn't a thing Mac apps have: Mail
/// says how its accounts stand in the same place. The window's size and the sidebar's width are
/// AppKit's to remember, by autosave name.
/// </remarks>
internal sealed class MainWindowController : NSWindowController
{
    private const string FrameName = "Main";
    private const string SyncItemId = "Sync";

    private readonly MainPresenter _presenter;
    private readonly SyncScheduler _scheduler;
    private readonly ILog _log;
    private readonly SidebarController _sidebar;
    private readonly TaskListController _tasks = new();
    private readonly CancellationTokenSource _cts = new();

    private NSTimer? _ticker;
    private string? _failure;

    public MainWindowController(MainPresenter presenter, SyncScheduler scheduler, ILog log)
        : base(new NSWindow(
            new CGRect(0, 0, 980, 640),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable
                | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
            NSBackingStore.Buffered,
            deferCreation: false))
    {
        _presenter = presenter;
        _scheduler = scheduler;
        _log = log;
        _sidebar = new SidebarController(key => _presenter.SelectByKey(key));

        var split = new NSSplitViewController();
        var sidebarItem = NSSplitViewItem.CreateSidebar(_sidebar);
        sidebarItem.MinimumThickness = 180;
        sidebarItem.MaximumThickness = 360;
        split.AddSplitViewItem(sidebarItem);
        split.AddSplitViewItem(NSSplitViewItem.FromViewController(_tasks));
        split.SplitView.AutosaveName = "MainSplit";

        var window = Window;
        window.ContentViewController = split;
        window.MinSize = new CGSize(600, 380);
        window.ToolbarStyle = NSWindowToolbarStyle.Unified;
        window.Toolbar = new NSToolbar("Main")
        {
            Delegate = new Toolbar(),
            DisplayMode = NSToolbarDisplayMode.Icon,
            AllowsUserCustomization = false,
        };

        // The frame AppKit saved last time, or a sensible one centred on a first launch.
        if (!window.SetFrameUsingName(FrameName))
        {
            window.SetContentSize(new CGSize(980, 640));
            window.Center();
        }

        window.FrameAutosaveName = FrameName;
    }

    /// <summary>Shows the cache straight away, then syncs, then hands over to the background loop.</summary>
    /// <param name="savedKey">The view the last session ended on</param>
    public async void Start(string? savedKey)
    {
        _presenter.RestoreView(savedKey);
        _presenter.RowsChanged += OnRowsChanged;
        _presenter.StatusChanged += OnStatusChanged;
        _scheduler.SyncFailed += OnSyncFailed;
        Render();
        Window.MakeFirstResponder(_tasks.FocusView);

        // Republished each minute so "Synced 3m ago" keeps telling the truth between syncs.
        _ticker = NSTimer.CreateRepeatingScheduledTimer(60, _ => _presenter.PublishStatus());

        try
        {
            await _presenter.LoadAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TodoistAuthException)
        {
            // The engine has let go of the token, and the presenter says so from here on.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            OnSyncFailed(ex);
        }

        _scheduler.Start();
    }

    /// <summary>Stops listening, so nothing arrives for a window on its way out.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _ticker?.Invalidate();
        _presenter.RowsChanged -= OnRowsChanged;
        _presenter.StatusChanged -= OnStatusChanged;
        _scheduler.SyncFailed -= OnSyncFailed;
    }

    [Export(MainMenu.ShowToday)]
    private void ShowToday(NSObject sender) => _presenter.Select(ViewSelection.Of(SmartView.Today));

    [Export(MainMenu.ShowUpcoming)]
    private void ShowUpcoming(NSObject sender) => _presenter.Select(ViewSelection.Of(SmartView.Upcoming));

    [Export(MainMenu.ShowInbox)]
    private void ShowInbox(NSObject sender) => _presenter.Select(ViewSelection.Of(SmartView.Inbox));

    [Export(MainMenu.SyncNow)]
    private void SyncNow(NSObject sender) => _scheduler.RequestNow();

    [Export(MainMenu.OpenProjectPage)]
    private void OpenProjectPage(NSObject sender)
        => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl("https://github.com/tridian-tn/termyn"));

    // Raised on whichever thread published: the main thread for something the user did, the
    // sync loop's for a background sync. AppKit is only to be touched from the main thread, so
    // both go there, as the Windows window sends both through BeginInvoke.
    private void OnRowsChanged() => BeginInvokeOnMainThread(Render);

    private void OnStatusChanged() => BeginInvokeOnMainThread(RenderStatus);

    private void OnSyncFailed(Exception ex)
    {
        _log.Error("A sync failed.", ex);
        BeginInvokeOnMainThread(() =>
        {
            _failure = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.SyncFailed, ex.Message);
            RenderStatus();
        });
    }

    private void Render()
    {
        _sidebar.Render(_presenter.Sidebar, _presenter.SelectedKey);
        _tasks.Render(_presenter.Rows, _presenter.LabelColours, _presenter.UnsupportedFilter, _presenter.Today);

        // A fresh publish is a sync that got somewhere, so whatever failed before has passed.
        _failure = null;
        RenderStatus();
    }

    private void RenderStatus()
    {
        var crumbs = _presenter.Breadcrumbs;
        Window.Title = crumbs.Count > 0 ? crumbs[^1].Label : "Termyn";
        Window.Subtitle = _failure ?? _presenter.Status;
        _tasks.ShowReconnectNeeded(_presenter.ReconnectNeeded);
    }

    /// <summary>
    /// The toolbar: the sidebar's own toggle over the sidebar, and Sync over the list, either side
    /// of the separator that tracks the sidebar's edge.
    /// </summary>
    private sealed class Toolbar : NSToolbarDelegate
    {
        private static readonly string[] Items =
        [
            NSToolbar.NSToolbarToggleSidebarItemIdentifier,
            NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
            NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
            SyncItemId,
        ];

        public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => Items;

        public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => Items;

        public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
            => itemIdentifier == SyncItemId
                ? new NSToolbarItem(SyncItemId)
                {
                    Image = NSImage.GetSystemSymbol("arrow.clockwise", Strings.ToolbarSync),
                    Label = Strings.ToolbarSync,
                    ToolTip = Strings.ToolbarSyncTip,
                    Action = new ObjCRuntime.Selector(MainMenu.SyncNow),
                    Bordered = true,
                }
                : new NSToolbarItem(itemIdentifier);
    }
}
