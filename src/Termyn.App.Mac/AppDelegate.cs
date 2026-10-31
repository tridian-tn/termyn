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
    private TokenWindowController? _tokenWindow;
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
        // that account read-only.
        var auth = new AuthPresenter(api, secrets);
        if (!auth.HasStoredToken && !_engine.AwaitingToken)
        {
            _tokenWindow = new TokenWindowController(auth, _log, OpenMainWindow);
            _tokenWindow.ShowWindow(this);
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

        _presenter = new MainPresenter(engine, new QuickAddParser(new SystemClock()), log: _log);
        _scheduler = new SyncScheduler(_presenter.SyncAsync, _settings.Cadence);

        // Nothing writes yet, but the engine is told about the loop the way the Windows app tells
        // it, so the first command that queues a change is heard without anything changing here.
        engine.Queued += _scheduler.NotifyWrite;

        _mainWindow = new MainWindowController(_presenter, _scheduler, _log);
        _mainWindow.ShowWindow(this);
        _mainWindow.Start(_settings.View.SelectedKey);
        _tokenWindow?.Close();
        _tokenWindow = null;
    }

    /// <summary>The running version, as a release tag is written.</summary>
    private static string Version
        => UpdateResult.Tag(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
}
