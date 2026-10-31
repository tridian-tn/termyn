using System.Globalization;
using Termyn.Core.Api;
using Termyn.Core.Attachments;
using Termyn.Core.Capture;
using Termyn.Core.History;
using Termyn.Core.Logging;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Core.Update;
using Termyn.Platform.Windows;
using Termyn.Presentation;

namespace Termyn.App.Windows;

internal static class Program
{
    /// <summary>
    /// Where an exception nothing else caught is written down. Null until the log exists, which is a
    /// few lines into <see cref="Main"/>.
    /// </summary>
    private static FileLog? _log;

    [STAThread]
    private static void Main(string[] args)
    {
        // First of all, so nothing from here on can end the process without a line in the log. Before
        // any window, too: the framework only takes the mode while none exists.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnDying(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.Warn("A background task failed and nothing was waiting for it.", e.Exception);
            e.SetObserved();
        };

        // What throws before the message loop is running — building the window, the token dialog —
        // isn't the UI thread's to catch, and would otherwise go straight to the crash dialog.
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            _log?.Error("Termyn failed and has to close.", ex);
            Tell(Strings.UnexpectedErrorClosing, _log?.Path ?? ex.Message, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Something thrown on the UI thread that nothing caught. Written down and said, and the app
    /// carries on: everything the user does goes through the outbox, so a handler that fell over
    /// part-way has left nothing half-written behind it.
    /// </summary>
    /// <param name="ex">What was thrown</param>
    private static void OnUiException(Exception ex)
    {
        // Once per session, and only the first one written out in full. A failure that repeats — in
        // a paint or a timer, say — comes straight back the moment its box is closed, and a box per
        // time round would leave the app unusable rather than merely broken in one place. The log
        // still says how often it happened.
        _uiFailures++;
        if (_uiFailures > 1)
        {
            if (_uiFailures is 2 or 10 or 100 or 1000)
                _log?.Warn($"Something went wrong on the UI thread again ({_uiFailures} times so far): {ex.GetType().Name}: {ex.Message}");
            return;
        }

        _log?.Error("Something nothing expected went wrong on the UI thread. Termyn carried on.", ex);
        Tell(Strings.UnexpectedError, _log?.Path ?? ex.Message, MessageBoxIcon.Warning);
    }

    /// <summary>How many failures have reached the UI thread's handler this session.</summary>
    private static int _uiFailures;

    /// <summary>
    /// Something thrown on another thread that nothing caught. The runtime ends the process once
    /// this returns, so all that can be done is to say why in the log first.
    /// </summary>
    /// <param name="ex">What was thrown, when it was an exception at all</param>
    private static void OnDying(Exception? ex)
        => _log?.Error("Something nothing expected went wrong off the UI thread, and Termyn is ending.", ex);

    /// <summary>Puts up a box about a failure, without letting that become a failure of its own.</summary>
    /// <param name="format">What to say, with {0} for where the details are</param>
    /// <param name="details">The log's path, or the error itself when there's no log yet</param>
    /// <param name="icon">How serious it is</param>
    private static void Tell(string format, string details, MessageBoxIcon icon)
    {
        try
        {
            MessageBox.Show(string.Format(CultureInfo.CurrentCulture, format, details), "Termyn", MessageBoxButtons.OK, icon);
        }
        catch (Exception ex)
        {
            _log?.Warn("The box saying something went wrong couldn't be shown.", ex);
        }
    }

    private static void Run(string[] args)
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

        // Early, so anything that goes wrong from here on has somewhere to go. Nothing of the
        // account's is ever written to it — see ILog.
        var log = new FileLog(paths);
        _log = log;
        log.Info($"Termyn {AppVersion.Tag} starting on {Environment.OSVersion.VersionString}, .NET {Environment.Version}.");

        // Before any window exists, which is the only time the framework will take it.
        Theme.ApplyToFramework(settings.Theme);
        ApplicationConfiguration.Initialize();

        // Deliberately no MaxResponseContentBufferSize: it only applies to responses HttpClient
        // buffers for you, and every call here reads the stream itself, so setting it would have
        // promised a bound that was never in force. The update check bounds its own read; the
        // Todoist body is the account's own data and is parsed straight off the stream.
        //
        // No overall timeout: it would span an upload's body and cut off a large file. Each caller
        // bounds its own calls instead (TodoistHttp says more).
        using var http = TodoistHttp.CreateClient();
        ITodoistApi api = new TodoistApiClient(http);
        ISecretStore secrets = new DpapiSecretStore(paths);

        // The same HttpClient as the API: one connection pool, one place to configure.
        // The Todoist token rides on each request rather than on the client, so nothing of the
        // account's goes to GitHub with this.
        var updates = new GitHubReleaseCheck(http);

        var launch = new Launch(paths, settingsStore, api, secrets, instance, updates, log);

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
        GitHubReleaseCheck Updates,
        ILog Log);

    /// <summary>
    /// Runs one signed-in stretch: asks for a token if there isn't one, then shows the window until
    /// it closes.
    /// </summary>
    /// <param name="launch">What the process holds across sessions</param>
    /// <param name="settings">The settings to start this session with</param>
    /// <param name="tray">Start with no window on screen</param>
    /// <param name="quickAdd">Open the quick-add box straight away</param>
    /// <returns>
    /// True when the window closed because the user signed out or switched account, so there's
    /// another session to run
    /// </returns>
    private static bool RunSession(Launch launch, AppSettings settings, bool tray, bool quickAdd)
    {
        var (paths, settingsStore, api, secrets, instance, updates, log) = launch;

        var auth = new AuthPresenter(api, secrets);

        var store = new SqliteSnapshotStore(Path.Combine(paths.CacheDirectory, "cache.db"));
        using var storeHeld = new Held(store);

        var engine = new SyncEngine(api, store, secrets, log: log);
        engine.Load();

        // After the load rather than before it: a damaged page is often only found by reading it,
        // and the store rebuilds itself there too.
        if (store.Rebuilt)
            log.Warn("The cache couldn't be read and was started again from nothing. Anything queued went with it.");

        // Loaded first, because the cache decides what having no token means. With nothing held
        // it's a first start, or one after signing out. Over an account it's a token Todoist turned
        // away, and the window opens on that account read-only and asks for a new one there, where
        // it can be checked against the account it would carry on with.
        var firstRun = !auth.HasStoredToken && !engine.AwaitingToken;
        if (firstRun)
        {
            using var tokenForm = new TokenEntryForm(auth);
            if (tokenForm.ShowDialog() != DialogResult.OK)
                return false;
        }

        // Swept on the way up rather than only after a download: an app left closed for a month
        // should not open holding a month-old cache it never got the chance to tidy.
        var attachments = new AttachmentCache(paths.AttachmentDirectory, settings.AttachmentCache);
        attachments.Sweep();

        // Signing out, or a different account's token, wipes the account's tasks; its downloaded
        // files go the same way, so a machine that has switched accounts isn't still holding the
        // previous one's documents.
        engine.Purged += () => attachments.Clear();

        // Its own file beside the cache rather than a table in it: the cache is thrown away and
        // rebuilt whenever it can't be read, and a record of what the user did has no business
        // going with it. It does go with the account, which the presenter sees to.
        var history = new SqliteHistoryStore(Path.Combine(paths.CacheDirectory, "history.db"));
        using var historyHeld = new Held(history);

        // A first run should find both empty. They aren't if a rejected token kept them for an
        // account whose cache has since gone, unreadable or deleted, and the token just given could
        // be anybody's.
        if (firstRun)
        {
            attachments.Clear();

            // Not a reason to stop starting, any more than a history that can't be written to is.
            try
            {
                history.Clear();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Warn("The history left from before couldn't be cleared.", ex);
            }
        }

        var presenter = new MainPresenter(
            engine,
            new QuickAddParser(new SystemClock()),
            fetcher: new AttachmentFetcher(api, secrets, attachments, new ZoneIdentifierMarker(log)),
            history: history,
            log: log);
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

        var shell = new Shell(paths, settingsStore, settings, hotkey, autoStart, notifier, instance, updates, log, tray, quickAdd, store.Rebuilt, auth);

        bool signedOut;
        TokenCheck? switchTo;
        try
        {
            using var form = new MainForm(presenter, scheduler, shell);
            Application.Run(form);
            signedOut = form.SignedOut;
            switchTo = form.SwitchTo;
        }
        finally
        {
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // The loop gives a sync five seconds to notice it's been called off, then stops waiting. One
        // still going is using the cache, so it's given longer, though not for ever. If it still
        // hasn't finished, nothing is closed or wiped under it: the files are left for the process
        // to close as it ends, and it does end, since a next session would open them a second time.
        if (!WaitForSyncs(presenter, TimeSpan.FromSeconds(15)))
        {
            storeHeld.Abandoned = true;
            historyHeld.Abandoned = true;
            log.Warn("A sync was still running after the loop stopped. The cache and history were left for the process to close.");

            if (signedOut)
            {
                log.Error(switchTo is null
                    ? "Signing out couldn't finish, so the account is still signed in."
                    : "Switching account couldn't finish, so the last account is still held.");

                MessageBox.Show(
                    string.Format(CultureInfo.CurrentCulture, switchTo is null ? Strings.SignOutFailed : Strings.SwitchAccountFailed, Strings.SyncStillRunning),
                    "Termyn",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return false;
        }

        if (!signedOut)
            return false;

        // Only now the loop has stopped, so nothing can start a sync on the token while it goes. One
        // still on its way back after the loop's bounded wait is dropped by the engine when it
        // lands, rejection included, so it can't reach the next account's token.
        try
        {
            // Switching is the same wipe with the new token stored after it, which the engine only
            // does once the wipe has worked.
            if (switchTo is { } next)
            {
                presenter.Reconnect(next.Token, next.UserId);
                log.Info("Switched to another account. The last one's cache, queue, downloaded files and history are gone.");
            }
            else
            {
                presenter.SignOut();
                log.Info("Signed out. The token, the cache, the queue, the downloaded files and the history are gone.");
            }

            // Emptying the cache skips a file another program has open, rather than failing over it.
            // Signing out still goes ahead, but not quietly: the file is the last account's.
            if (attachments.Size() > 0)
            {
                MessageBox.Show(
                    string.Format(CultureInfo.CurrentCulture, Strings.DownloadsLeftBehind, paths.AttachmentDirectory),
                    "Termyn",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && switchTo is not null)
        {
            log.Error("Switching account couldn't finish, so the last account is still held.", ex);

            // The new token is stored only once the cache is empty, so the next session opens on
            // the last account, still waiting for a token of its own.
            MessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, Strings.SwitchAccountFailed, ex.Message),
                "Termyn",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error("Signing out couldn't finish, so the account is still signed in.", ex);

            // The engine lets go of the token only once the cache is empty, so a failure here
            // leaves the account signed in over data it can still sync, and the next session opens
            // straight onto it.
            MessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, Strings.SignOutFailed, ex.Message),
                "Termyn",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        return true;
    }

    /// <summary>
    /// Disposes what it holds when the session ends, unless shutdown found something still using it.
    /// </summary>
    /// <param name="held">The store to dispose</param>
    private sealed class Held(IDisposable held) : IDisposable
    {
        /// <summary>Set when a sync may still be using it, so it's left for the process to close.</summary>
        public bool Abandoned { get; set; }

        public void Dispose()
        {
            if (!Abandoned)
                held.Dispose();
        }
    }

    /// <summary>
    /// Waits for the presenter's syncs to finish, keeping this thread's messages moving meanwhile.
    /// </summary>
    /// <remarks>
    /// The first sync was started from the window, so what's left of it comes back to this thread to
    /// run, and the message loop that would have run it has already ended. Blocked outright, the
    /// wait could only ever time out.
    /// </remarks>
    /// <param name="presenter">The presenter whose syncs to wait for</param>
    /// <param name="timeout">How long to wait at most</param>
    /// <returns>False when one was still running at the end of it</returns>
    private static bool WaitForSyncs(MainPresenter presenter, TimeSpan timeout)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (!presenter.WaitForSyncs(TimeSpan.FromMilliseconds(50)))
        {
            if (waited.Elapsed >= timeout)
                return false;

            Application.DoEvents();
        }

        return true;
    }

    private static bool Has(string[] args, string flag)
        => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
