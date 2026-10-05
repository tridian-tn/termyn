using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// That the sidebar keeps a project made here lit once the server has named it.
/// </summary>
/// <remarks>
/// The row it was opened from is gone by then, replaced by one under the server's id, so the tree
/// is rebuilt without anything matching what it had selected.
/// </remarks>
public class SidebarRenameTests : IDisposable
{
    private readonly string _config = Path.Combine(
        Path.GetTempPath(),
        $"termyn-rename-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_config))
            File.Delete(_config);

        GC.SuppressFinalize(this);
    }

    [WinFormsFact]
    public async Task Opened_from_Favourites_it_stays_lit_there()
    {
        // Lighting the copy down in the tree instead would put the highlight somewhere the user
        // never clicked, and the next start would open on that row rather than this one.
        var api = new FakeApi();
        using var window = TestWindow.Start(_config, new InMemorySnapshotStore(), api, out var presenter);
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-32000, -32000);
        window.ShowInTaskbar = false;
        window.Show();

        presenter.AddProject("Errands");
        var made = presenter.Sidebar.First(n => n.Kind == SidebarKind.Project && n.Label == "Errands").Id;
        presenter.ToggleProjectFavorite(made);
        TestWindow.Click(window, SidebarKeys.Favourite(SidebarKind.Project, made));

        api.Next = FakeApi.Naming(
            new Dictionary<string, string> { ["project_add"] = "p2" },
            Json.Change("projects", "p2", """{"id":"p2","name":"Errands","child_order":1,"is_favorite":true}"""));
        await presenter.SyncAsync();

        var favourite = SidebarKeys.Favourite(SidebarKind.Project, "p2");
        Assert.Equal(favourite, TestWindow.Highlighted(window));
        Assert.Contains($"\"selectedKey\": \"{favourite}\"", File.ReadAllText(_config));
    }
}
