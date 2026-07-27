using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Platform;
using Termyn.Core.Sync;
using Termyn.Platform.Windows;
using Termyn.Presentation;

namespace Termyn.App.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            // Bound the response we will buffer, so a runaway or hostile body can't exhaust memory.
            MaxResponseContentBufferSize = 128 * 1024 * 1024,
        };
        ITodoistApi api = new TodoistApiClient(http);
        IAppPaths paths = new WindowsAppPaths();
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
        var scheduler = new SyncScheduler(presenter.SyncAsync);

        try
        {
            Application.Run(new MainForm(presenter, scheduler));
        }
        finally
        {
            scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
