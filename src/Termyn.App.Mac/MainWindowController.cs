using System.Globalization;
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
///
/// The menus reach the window through the responder chain, and it answers the ones about tasks
/// with <see cref="TaskCommands"/>, validating each before it's shown so its name, tick and
/// whether it's enabled are the presenter's.
/// </remarks>
internal sealed class MainWindowController : NSWindowController
{
    private const string FrameName = "Main";
    private const string NewTaskItemId = "NewTask";
    private const string SyncItemId = "Sync";

    private readonly MainPresenter _presenter;
    private readonly SyncScheduler _scheduler;
    private readonly ILog _log;
    private readonly AuthPresenter _auth;
    private readonly Action<TokenCheck> _switchAccount;
    private readonly TaskCommands _commands;
    private readonly SidebarController _sidebar;
    private readonly TaskListController _tasks;
    private readonly Toolbar _toolbar;
    private readonly CancellationTokenSource _cts = new();

    private NSTimer? _ticker;
    private NSTimer? _lingering;
    private NSPopover? _popover;
    private string? _notice;
    private NSObject? _activated;
    private NSObject? _woke;

    /// <summary>
    /// Set while the last sync found the token stored but out of reach — the Keychain locked, or
    /// its access prompt unanswered — and cleared by the first sync that gets through.
    /// </summary>
    private bool _waitingForToken;

    /// <param name="switchAccount">Forgets this account for another's token, once the user has agreed</param>
    public MainWindowController(MainPresenter presenter, SyncScheduler scheduler, AuthPresenter auth, ILog log, Action<TokenCheck> switchAccount)
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
        _auth = auth;
        _switchAccount = switchAccount;
        _commands = new TaskCommands(presenter, log, Say)
        {
            OpenDue = OpenDue,
            OpenNewTask = OpenNewTask,
        };
        _sidebar = new SidebarController(key => _presenter.SelectByKey(key), _commands);
        _tasks = new TaskListController(_commands);

        var split = new NSSplitViewController();
        var sidebarItem = NSSplitViewItem.CreateSidebar(_sidebar);
        sidebarItem.MinimumThickness = 180;
        sidebarItem.MaximumThickness = 360;
        split.AddSplitViewItem(sidebarItem);
        split.AddSplitViewItem(NSSplitViewItem.FromViewController(_tasks));
        split.SplitView.AutosaveName = "MainSplit";

        _toolbar = new Toolbar();
        var window = Window;
        window.ContentViewController = split;
        window.MinSize = new CGSize(600, 380);
        window.ToolbarStyle = NSWindowToolbarStyle.Unified;
        window.Toolbar = new NSToolbar("Main")
        {
            Delegate = _toolbar,
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

        // A token out of reach is most often a Keychain waiting on its prompt, or a Mac waiting to
        // be unlocked, so coming back to the app or the Mac waking is the moment to try again —
        // rather than leaving it to the next round of the loop.
        _activated = NSNotificationCenter.DefaultCenter.AddObserver(NSApplication.DidBecomeActiveNotification, _ => RetryForToken());
        _woke = NSWorkspace.SharedWorkspace.NotificationCenter.AddObserver(NSWorkspace.DidWakeNotification, _ => RetryForToken());

        try
        {
            await _presenter.LoadAsync(_cts.Token);
            Synced();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TodoistAuthException)
        {
            // Turned away, which the presenter says from here on; or out of reach, which is said
            // until it can be read.
            if (!_presenter.ReconnectNeeded)
                WaitForToken();
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
        _lingering?.Invalidate();
        if (_activated is not null)
            NSNotificationCenter.DefaultCenter.RemoveObserver(_activated);
        if (_woke is not null)
            NSWorkspace.SharedWorkspace.NotificationCenter.RemoveObserver(_woke);
        _presenter.RowsChanged -= OnRowsChanged;
        _presenter.StatusChanged -= OnStatusChanged;
        _scheduler.SyncFailed -= OnSyncFailed;
    }

    [Export(TaskMenu.Action)]
    private void RunTaskCommand(NSMenuItem sender) => _commands.Run(TaskMenu.CommandOf(sender), _tasks.SelectedId);

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

    [Export(NewTaskAction)]
    private void NewTaskClicked(NSObject sender) => _commands.Run(AppCommand.NewTask, null);

    /// <summary>
    /// Fills in a task item as its menu is about to show — or its key is about to be pressed — and
    /// says whether it can run. Everything else this window answers can always run.
    /// </summary>
    [Export("validateMenuItem:")]
    private bool ValidateMenuItem(NSMenuItem item)
    {
        if (item.Action?.Name == MainMenu.Reconnect)
            return _presenter.ReconnectNeeded;

        if (item.Action?.Name != TaskMenu.Action)
            return true;

        var command = TaskMenu.CommandOf(item);
        var id = _tasks.SelectedId;
        var state = _commands.State(TaskCommands.Gate(command), id);

        item.Title = TaskCommands.Label(command, state, _commands.Row(id));
        item.State = state.Checked ? NSCellStateValue.On : NSCellStateValue.Off;

        // The day a Schedule entry lands on, under its name. Subtitles came in macOS 14.4; before
        // that the entry still works, and the day is in its tooltip.
        if (OperatingSystem.IsMacOSVersionAtLeast(14, 4))
            item.Subtitle = state.Hint ?? string.Empty;
        item.ToolTip = state.Hint;
        return state.Enabled;
    }

    /// <summary>The + in the toolbar, enabled as New Task is.</summary>
    [Export("validateToolbarItem:")]
    private bool ValidateToolbarItem(NSToolbarItem item)
        => item.Identifier != NewTaskItemId || _commands.State(TaskCommands.Gate(AppCommand.NewTask), null).Enabled;

    private const string NewTaskAction = "newTask:";

    // Raised on whichever thread published: the main thread for something the user did, the
    // sync loop's for a background sync. AppKit is only to be touched from the main thread, so
    // both go there, as the Windows window sends both through BeginInvoke.
    private void OnRowsChanged() => BeginInvokeOnMainThread(Render);

    private void OnStatusChanged() => BeginInvokeOnMainThread(RenderStatus);

    private void OnSyncFailed(Exception ex)
    {
        // A token Todoist turned away is said by the banner; one out of reach is waited out. Neither
        // is a failure worth a line in the log every time the loop comes round.
        if (ex is TodoistAuthException)
        {
            BeginInvokeOnMainThread(() =>
            {
                if (!_presenter.ReconnectNeeded)
                    WaitForToken();
            });
            return;
        }

        _log.Error("A sync failed.", ex);
        BeginInvokeOnMainThread(() => Say(string.Format(CultureInfo.CurrentCulture, Strings.SyncFailed, ex.Message)));
    }

    /// <summary>
    /// Told after every sync that got through, from whichever thread ran it — which means the token
    /// was there to send, so anything said about waiting for it is over.
    /// </summary>
    public void Synced() => BeginInvokeOnMainThread(() =>
    {
        if (!_waitingForToken)
            return;

        _waitingForToken = false;
        _log.Info("The token can be read again, and syncing has carried on.");
        RenderStatus();
    });

    /// <summary>The token is stored but can't be read yet: say so, quietly, and wait for it.</summary>
    private void WaitForToken()
    {
        if (!_waitingForToken)
            _log.Info("The token is stored but can't be read yet, so syncing waits for it.");

        _waitingForToken = true;
        RenderStatus();
    }

    private void RetryForToken()
    {
        if (_waitingForToken)
            _scheduler.RequestNow();
    }

    /// <summary>
    /// Says something in the subtitle until the next publish replaces it — the window's equivalent
    /// of the line the Windows status bar writes over.
    /// </summary>
    private void Say(string notice)
    {
        _notice = notice;
        RenderStatus();
    }

    private void Render()
    {
        _sidebar.Render(_presenter.Sidebar, _presenter.SelectedKey);
        _tasks.Render(_presenter.Rows, _presenter.LabelColours, _presenter.UnsupportedFilter, _presenter.Today);

        // A fresh publish is the model moving on, so whatever was said about the last one has passed.
        _notice = null;
        RenderStatus();
        Linger();
    }

    private void RenderStatus()
    {
        var crumbs = _presenter.Breadcrumbs;
        Window.Title = crumbs.Count > 0 ? crumbs[^1].Label : "Termyn";
        Window.Subtitle = _notice ?? (_waitingForToken && !_presenter.ReconnectNeeded ? Strings.TokenOutOfReach : _presenter.Status);
        RenderBanner();
    }

    /// <summary>
    /// The strip over the list: a token Todoist has turned away comes first, since nothing can be
    /// changed until it's settled; changes Todoist refused come next, with the way to review them.
    /// </summary>
    private void RenderBanner()
    {
        if (_presenter.ReconnectNeeded)
        {
            _tasks.ShowBanner(Strings.ReconnectNeeded, Strings.ReconnectButton, Reconnect);
            return;
        }

        var failed = _presenter.SyncStatus.Failed;
        if (failed > 0)
        {
            var text = failed == 1
                ? Strings.FailedOne
                : string.Format(CultureInfo.CurrentCulture, Strings.FailedMany, failed);
            _tasks.ShowBanner(text, Strings.FailedReview, ReviewFailures);
            return;
        }

        _tasks.ShowBanner(null);
    }

    /// <summary>
    /// Ticks once a second while something just ticked off is still on the list, so it goes when
    /// its moment is up — and not at all otherwise, so an idle window isn't woken for nothing.
    /// </summary>
    private void Linger()
    {
        if (_presenter.Lingering && _lingering is null)
        {
            _lingering = NSTimer.CreateRepeatingScheduledTimer(1, _ => _commands.Guarded(() => _presenter.DropTicked()));
        }
        else if (!_presenter.Lingering && _lingering is not null)
        {
            _lingering.Invalidate();
            _lingering = null;
        }
    }

    /// <summary>
    /// Asks for a new token once Todoist has turned the last one away, as a sheet over the window.
    /// </summary>
    /// <remarks>
    /// The same account's token is taken in place, and what was held is sent straight away. Another
    /// account's is a sign-out followed by a sign-in, so it's asked about first, in the presenter's
    /// own words, which say what would be lost.
    /// </remarks>
    [Export(MainMenu.Reconnect)]
    private void Reconnect(NSObject sender) => Reconnect();

    private void Reconnect()
    {
        if (!_presenter.ReconnectNeeded || ContentViewController is not { } content)
            return;

        var asking = new TokenController(_auth, _log, reconnecting: true);
        asking.Cancelled += () => content.DismissViewController(asking);
        asking.Accepted += check =>
        {
            content.DismissViewController(asking);
            TakeToken(check);
        };
        content.PresentViewControllerAsSheet(asking);
    }

    private void TakeToken(TokenCheck check)
    {
        if (_presenter.IsSameAccount(check.UserId))
        {
            if (_commands.Guarded(() => _presenter.Reconnect(check.Token, check.UserId)))
                _scheduler.RequestNow();
            return;
        }

        var question = new NSAlert
        {
            AlertStyle = NSAlertStyle.Warning,
            MessageText = Strings.SwitchAccountTitle,
            InformativeText = _presenter.SwitchAccountQuestion(check.UserId),
        };
        question.AddButton(Strings.SwitchAccount).HasDestructiveAction = true;
        question.AddButton(Strings.Cancel);
        question.BeginSheetForResponse(Window, response =>
        {
            if (response == (nint)(long)NSAlertButtonReturn.First)
                _switchAccount(check);
        });
    }

    private void ReviewFailures()
        => ContentViewController?.PresentViewControllerAsSheet(new FailedChangesController(_presenter, _commands));

    /// <summary>Opens the new-task popover from the toolbar's +, or over the list if the toolbar's hidden.</summary>
    private void OpenNewTask()
    {
        var content = new NewTaskController(_presenter, _log, ClosePopover);
        content.Prepare();
        var popover = Popover(content);

        if (Window.Toolbar is { Visible: true } && _toolbar.NewTaskItem is { } item)
            popover.ShowRelative(item);
        else
            popover.Show(_tasks.View.Bounds, _tasks.View, NSRectEdge.MaxYEdge);
    }

    /// <summary>Opens the due-date popover beside the task's row.</summary>
    private void OpenDue(string id)
    {
        if (_tasks.AnchorFor(id) is not { } anchor)
            return;

        Popover(new DueController(_presenter, _commands, id, ClosePopover)).Show(anchor.Rect, anchor.View, NSRectEdge.MaxYEdge);
    }

    /// <summary>
    /// A popover that closes when the user clicks elsewhere, as every popover on a Mac does, and
    /// hands the keyboard back to the list when it goes.
    /// </summary>
    private NSPopover Popover(NSViewController content)
    {
        _popover?.Close();
        _popover = new NSPopover
        {
            ContentViewController = content,
            Behavior = NSPopoverBehavior.Transient,
            Animates = true,
        };
        _popover.Delegate = new Closing(() => Window.MakeFirstResponder(_tasks.FocusView));
        return _popover;
    }

    private void ClosePopover()
    {
        _popover?.Close();
        _popover = null;
    }

    /// <summary>Says when a popover has closed, however it was closed.</summary>
    private sealed class Closing(Action closed) : NSPopoverDelegate
    {
        public override void DidClose(NSNotification notification) => closed();
    }

    /// <summary>
    /// The toolbar: the sidebar's own toggle over the sidebar; over the list, + to add a task, as
    /// Reminders has it, and Sync, either side of the separator that tracks the sidebar's edge.
    /// </summary>
    private sealed class Toolbar : NSToolbarDelegate
    {
        private static readonly string[] Items =
        [
            NSToolbar.NSToolbarToggleSidebarItemIdentifier,
            NSToolbar.NSToolbarSidebarTrackingSeparatorItemIdentifier,
            NSToolbar.NSToolbarFlexibleSpaceItemIdentifier,
            SyncItemId,
            NewTaskItemId,
        ];

        /// <summary>The + item, once the toolbar has made it, for the new-task popover to point from.</summary>
        public NSToolbarItem? NewTaskItem { get; private set; }

        public override string[] AllowedItemIdentifiers(NSToolbar toolbar) => Items;

        public override string[] DefaultItemIdentifiers(NSToolbar toolbar) => Items;

        public override NSToolbarItem WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted)
        {
            switch (itemIdentifier)
            {
                case SyncItemId:
                    return Item(SyncItemId, "arrow.clockwise", Strings.ToolbarSync, Strings.ToolbarSyncTip, MainMenu.SyncNow);

                case NewTaskItemId:
                    NewTaskItem = Item(NewTaskItemId, "plus", Strings.MenuNewTask, Strings.NewTaskTip, NewTaskAction);
                    return NewTaskItem;

                default:
                    return new NSToolbarItem(itemIdentifier);
            }
        }

        private static NSToolbarItem Item(string id, string symbol, string label, string tip, string action) => new(id)
        {
            Image = NSImage.GetSystemSymbol(symbol, label),
            Label = label,
            ToolTip = tip,
            Action = new ObjCRuntime.Selector(action),
            Bordered = true,
        };
    }
}
