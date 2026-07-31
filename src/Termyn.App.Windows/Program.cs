using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Core.Update;
using Termyn.Platform.Windows;
using Termyn.Presentation;

namespace Termyn.App.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var quickAdd = Has(args, "--quick-add");
        var tray = quickAdd || Has(args, "--tray");

        using var instance = new WindowsSingleInstance();
        if (!instance.TryAcquire())
        {
            // Two processes would share one cache and outbox, and the second would fail to take the
            // global hotkey. Hand over what this launch was asked to do and get out of the way.
            instance.TrySignal(quickAdd ? InstanceSignals.QuickAdd : InstanceSignals.Show);
            return;
        }

        IAppPaths paths = new WindowsAppPaths();
        var settingsStore = new SettingsStore(paths);
        var settings = settingsStore.Load();

        // Before any window exists, which is the only time the framework will take it.
        Theme.ApplyToFramework(settings.Theme);
        ApplicationConfiguration.Initialize();

        // Deliberately no MaxResponseContentBufferSize: it only applies to responses HttpClient
        // buffers for you, and every call here reads the stream itself, so setting it would have
        // promised a bound that was never in force. The update check bounds its own read; the
        // Todoist body is the account's own data and is parsed straight off the stream.
        //
        // The timeout is likewise only half of one — it stops applying once the headers arrive, so
        // each caller puts a deadline on its own read as well.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        ITodoistApi api = new TodoistApiClient(http);
        ISecretStore secrets = new DpapiSecretStore(paths);

        var auth = new AuthPresenter(api, secrets);
        if (!auth.HasStoredToken)
        {
            using var tokenForm = new TokenEntryForm(auth);
            if (tokenForm.ShowDialog() != DialogResult.OK)
                return;
        }

        using var store = new SqliteSnapshotStore(Path.Combine(paths.CacheDirectory, "cache.db"));
        var engine = new SyncEngine(api, store, secrets);
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new SystemClock()));
        var scheduler = new SyncScheduler(presenter.SyncAsync, settings.Cadence);

        var autoStart = new WindowsAutoStart();
        settings = StartupReconciliation.OnLaunch(settingsStore, settings, autoStart);

        using var hotkey = new WindowsGlobalHotkey();

        // Not shown yet: the window puts it in the tray once it is up, so drawing the icon isn't on
        // the path to the first paint.
        using var notifier = new TrayNotifier();

        // The same HttpClient as the API: one connection pool, one timeout, one place to configure.
        // The Todoist token rides on each request rather than on the client, so nothing of the
        // account's goes to GitHub with this.
        var updates = new GitHubReleaseCheck(http);

        var shell = new Shell(paths, settingsStore, settings, hotkey, autoStart, notifier, instance, updates, tray, quickAdd);

        try
        {
            using var form = new MainForm(presenter, scheduler, shell);
            Application.Run(form);
        }
        finally
        {
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool Has(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
