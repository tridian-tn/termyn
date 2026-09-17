using Termyn.Core.Api;
using Termyn.Core.Attachments;
using Termyn.Core.Capture;
using Termyn.Core.History;
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

        // The same HttpClient as the API: one connection pool, one timeout, one place to configure.
        // The Todoist token rides on each request rather than on the client, so nothing of the
        // account's goes to GitHub with this.
        var updates = new GitHubReleaseCheck(http);

        var launch = new Launch(paths, settingsStore, api, secrets, instance, updates);

        // Signing out comes back round to the token dialog in this same process. Starting a fresh
        // one instead would race this one for the single-instance lock, and lose: it would hand
        // itself to a window that was already on its way out.
        while (RunSession(launch, settings, tray, quickAdd))
        {
            // Only the launch itself was asked to start in the tray or with quick-add open. The
            // settings are read again because the window that just closed has written to them —
            // and forgotten again here, since that write can fail, and a file that still names the
            // last account's labels and folds mustn't hand them to the next one.
            tray = quickAdd = false;
            settings = settingsStore.Load();
            settings = settings with { View = settings.View.WithoutAccount() };
        }
    }

    /// <summary>What lasts for the whole process, however many times the user signs in.</summary>
    private sealed record Launch(
        IAppPaths Paths,
        SettingsStore SettingsStore,
        ITodoistApi Api,
        ISecretStore Secrets,
        WindowsSingleInstance Instance,
        GitHubReleaseCheck Updates);

    /// <summary>
    /// Runs one signed-in stretch: asks for a token if there isn't one, then shows the window until
    /// it closes.
    /// </summary>
    /// <param name="launch">What the process holds across sessions</param>
    /// <param name="settings">The settings to start this session with</param>
    /// <param name="tray">Start with no window on screen</param>
    /// <param name="quickAdd">Open the quick-add box straight away</param>
    /// <returns>True when the window closed because the user signed out, so there's another session to run</returns>
    private static bool RunSession(Launch launch, AppSettings settings, bool tray, bool quickAdd)
    {
        var (paths, settingsStore, api, secrets, instance, updates) = launch;

        var auth = new AuthPresenter(api, secrets);
        if (!auth.HasStoredToken)
        {
            using var tokenForm = new TokenEntryForm(auth);
            if (tokenForm.ShowDialog() != DialogResult.OK)
                return false;
        }

        using var store = new SqliteSnapshotStore(Path.Combine(paths.CacheDirectory, "cache.db"));

        var engine = new SyncEngine(api, store, secrets);
        engine.Load();

        // Swept on the way up rather than only after a download: an app left closed for a month
        // should not open holding a month-old cache it never got the chance to tidy.
        var attachments = new AttachmentCache(paths.AttachmentDirectory, settings.AttachmentCache);
        attachments.Sweep();

        // Signing out or a rejected token wipes the account's tasks; its downloaded files go the
        // same way, so a machine that has switched accounts isn't still holding the previous one's
        // documents.
        engine.Purged += () => attachments.Clear();

        // Its own file beside the cache rather than a table in it: the cache is thrown away and
        // rebuilt whenever it can't be read, and a record of what the user did has no business
        // going with it. It does go with the account, which the presenter sees to.
        using var history = new SqliteHistoryStore(Path.Combine(paths.CacheDirectory, "history.db"));

        var presenter = new MainPresenter(
            engine,
            new QuickAddParser(new SystemClock()),
            fetcher: new AttachmentFetcher(api, secrets, attachments),
            history: history);
        var scheduler = new SyncScheduler(presenter.SyncAsync, settings.Cadence);

        // Said once, here, rather than at every place a write is made. The engine queues all of
        // them through one method and tells us from there, so a new kind of write cannot be added
        // without the loop hearing about it — which is what a comment managed for a while.
        engine.Queued += scheduler.NotifyWrite;

        var autoStart = new WindowsAutoStart();
        settings = StartupReconciliation.OnLaunch(settingsStore, settings, autoStart);

        using var hotkey = new WindowsGlobalHotkey();

        // Not shown yet: the window puts it in the tray once it is up, so drawing the icon isn't on
        // the path to the first paint.
        using var notifier = new TrayNotifier();

        var shell = new Shell(paths, settingsStore, settings, hotkey, autoStart, notifier, instance, updates, tray, quickAdd, store.Rebuilt);

        bool signedOut;
        try
        {
            using var form = new MainForm(presenter, scheduler, shell);
            Application.Run(form);
            signedOut = form.SignedOut;
        }
        finally
        {
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (!signedOut)
            return false;

        // Only now the loop has stopped, so nothing can start a sync on the token while it goes. One
        // still on its way back after the loop's bounded wait is dropped by the engine when it
        // lands, rejection included, so it can't reach the next account's token.
        try
        {
            presenter.SignOut();

            // Emptying the cache skips a file another program has open, rather than failing over it.
            // Signing out still goes ahead, but not quietly: the file is the last account's.
            if (attachments.Size() > 0)
            {
                MessageBox.Show(
                    "Some downloaded files couldn't be removed, probably because they're open in another "
                    + $"program. Close them and delete what's left in:\r\n\r\n{paths.AttachmentDirectory}",
                    "Termyn",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The engine lets go of the token only once the cache is empty, so a failure here
            // leaves the account signed in over data it can still sync, and the next session opens
            // straight onto it.
            MessageBox.Show(
                $"Termyn couldn't finish signing out, so you're still signed in.\r\n\r\n{ex.Message}",
                "Termyn",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        return true;
    }

    private static bool Has(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
