using Termyn.Core.Api;
using Termyn.Core.Platform;
using Termyn.Platform.Windows;
using Termyn.Presentation;

namespace Termyn.App.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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

        Application.Run(new MainForm(new MainPresenter(api, secrets)));
    }
}
