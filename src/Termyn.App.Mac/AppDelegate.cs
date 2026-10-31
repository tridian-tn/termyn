using System.Reflection;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Logging;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Core.Update;
using Termyn.Platform.Mac;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The composition root: puts together what a session needs, asks for a token if there isn't one,
/// and opens the window.
/// </summary>
/// <remarks>
/// The same sequence as <c>RunSession</c> in the Windows app's <c>Program</c>, less what the Mac app
/// doesn't do yet: no tray, hotkey or auto-start, no attachments or history, no signing out. macOS
/// keeps one copy of an app running by itself, so there's no single-instance lock either.
/// </remarks>
[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate
{
    // Deliberately no MaxResponseContentBufferSize, and only half a timeout, for the reasons given
    // where the Windows app makes the same client.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private SettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private ILog _log = null!;
    private SqliteSnapshotStore? _store;
    private SyncEngine? _engine;
    private MainPresenter? _presenter;
    private SyncScheduler? _scheduler;
    private AuthPresenter? _auth;
    private NSWindow? _tokenWindow;
    private MainWindowController? _mainWindow;

    public override void DidFinishLaunching(NSNotification notification)
    {
        NSApplication.SharedApplication.MainMenu = MainMenu.Build();

        IAppPaths paths = new MacAppPaths();
        _settingsStore = new SettingsStore(paths);
        _settings = _settingsStore.Load();

        // Early, so anything that goes wrong from here on has somewhere to go.
        _log = new FileLog(paths);
        _log.Info($"Termyn {Version} starting on macOS {NSProcessInfo.ProcessInfo.OperatingSystemVersionString}, .NET {Environment.Version}.");

        ITodoistApi api = new TodoistApiClient(_http);
        ISecretStore secrets = new KeychainSecretStore();

        _store = new SqliteSnapshotStore(Path.Combine(paths.CacheDirectory, "cache.db"));
        if (_store.Rebuilt)
            _log.Warn("The cache couldn't be read and was started again from nothing. Anything queued went with it.");

        _engine = new SyncEngine(api, _store, secrets, log: _log);
        _engine.Load();

        // As on Windows, the cache decides what having no token means: with nothing held it's a
        // first start; over an account it's a token Todoist turned away, and the window opens on
        // that account read-only. A token the Keychain holds but won't hand over yet counts as
        // a token: the window opens as usual and syncs once it can.
        _auth = new AuthPresenter(api, secrets);
        if (!_auth.HasStoredToken && !_engine.AwaitingToken)
        {
            var asking = new TokenController(_auth, _log, reconnecting: false);
            asking.Connected += OpenMainWindow;
            _tokenWindow = asking.InWindow();
            _tokenWindow.MakeKeyAndOrderFront(this);
        }
        else
        {
            OpenMainWindow();
        }
    }

    /// <summary>Closing the last window quits, as it does in a single-window app.</summary>
    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

    public override void WillTerminate(NSNotification notification)
    {
        _mainWindow?.Stop();

        // The last view is remembered here rather than by AppKit's window restoration, because
        // it's the presenter's key for it, and the Windows app keeps it in the same place.
        if (_presenter is not null && _settingsStore is not null)
            _settingsStore.Save(_settings with { View = _settings.View with { SelectedKey = _presenter.SelectedKey } });

        // The loop waits for a sync in flight, but only so long: quitting shouldn't hang on a slow
        // network.
        _scheduler?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store?.Dispose();
        _http.Dispose();
    }

    /// <summary>Opens the window on the account, from the cache first and then whatever a sync brings.</summary>
    private void OpenMainWindow()
    {
        var engine = _engine!;

        // The status line names the undo key, so it's told how a Mac writes one.
        _presenter = new MainPresenter(engine, new QuickAddParser(new SystemClock()), log: _log, shortcutText: MacShortcuts.Text);
        // Every sync that gets through had a token to send, which the window wants to hear when
        // it's been waiting for the Keychain to hand one over.
        var presenter = _presenter;
        _scheduler = new SyncScheduler(
            async ct =>
            {
                var outcome = await presenter.SyncAsync(ct);
                _mainWindow?.Synced();
                return outcome;
            },
            _settings.Cadence);

        // Every change queued is a reason to sync soon, as on Windows.
        engine.Queued += _scheduler.NotifyWrite;

        _mainWindow = new MainWindowController(_presenter, _scheduler, _auth!, _log, SwitchAccount);
        _mainWindow.ShowWindow(this);
        _mainWindow.Start(_settings.View.SelectedKey);
        _tokenWindow?.Close();
        _tokenWindow = null;
    }

    /// <summary>
    /// Forgets this account and goes on with another's token, once the user has agreed to.
    /// </summary>
    /// <remarks>
    /// A sign-out followed by a sign-in, as on Windows: the sync loop is stopped first, so nothing
    /// can sync on the token while it's swapped, and the window starts over on the new account,
    /// in its default view. The old window goes only once the new one is up, since closing the last
    /// window quits.
    /// </remarks>
    /// <param name="next">The other account's token, already accepted by Todoist</param>
    private void SwitchAccount(TokenCheck next)
    {
        var leaving = _mainWindow!;
        leaving.Stop();
        _engine!.Queued -= _scheduler!.NotifyWrite;
        _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();

        try
        {
            _presenter!.Reconnect(next.Token, next.UserId);
            _log.Info("Switched to another account. The last one's cache and queue are gone.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The new token is stored only once the cache is empty, so this leaves the last account
            // held, still waiting for a token of its own — which the window opens on.
            _log.Error("Switching account couldn't finish, so the last account is still held.", ex);
            new NSAlert
            {
                AlertStyle = NSAlertStyle.Warning,
                MessageText = Strings.SwitchAccountFailed,
                InformativeText = ex.Message,
            }.RunModal();
        }

        // The last account's view, folds and labels mean nothing to the next one.
        _settings = _settings with { View = _settings.View.WithoutAccount() };
        _settingsStore?.Save(_settings);

        OpenMainWindow();
        leaving.Close();
    }

    /// <summary>The running version, as a release tag is written.</summary>
    private static string Version
        => UpdateResult.Tag(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
}
