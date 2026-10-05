using Termyn.Core.Capture;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// A view of something made here, which goes by an id of our own until the server names it.
/// </summary>
/// <remarks>
/// The name comes back in the same sync that sends the thing, so a project opened as soon as it's
/// made is renamed out from under the view a few seconds later. The view has to go with it.
/// </remarks>
public class RenamedViewTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static (MainPresenter Presenter, FakeApi Api) Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");

        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
        return (presenter, api);
    }

    /// <summary>The id a row in the sidebar goes by, found by its name.</summary>
    private static string IdOf(MainPresenter presenter, SidebarKind kind, string name)
        => presenter.Sidebar.First(n => n.Kind == kind && n.Label == name).Id;

    [Fact]
    public async Task A_project_opened_as_soon_as_it_was_made_stays_open_once_the_server_names_it()
    {
        var (presenter, api) = Seeded();
        presenter.AddProject("Errands");
        presenter.Select(ViewSelection.OfProject(IdOf(presenter, SidebarKind.Project, "Errands")));

        // Offline, so it's filed here rather than parsed by the server.
        await presenter.CaptureAsync("Buy milk");

        api.Next = FakeApi.Naming(
            new Dictionary<string, string> { ["project_add"] = "p2", ["item_add"] = "i1" },
            Json.Change("projects", "p2", """{"id":"p2","name":"Errands","child_order":2}"""),
            Json.Change("items", "i1", """{"id":"i1","content":"Buy milk","project_id":"p2","child_order":1}"""));
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.OfProject("p2"), presenter.Selection);
        Assert.Equal(SidebarKeys.For(SidebarKind.Project, "p2"), presenter.SelectedKey);
        Assert.Equal(["Buy milk"], presenter.Rows.Select(r => r.Content));
    }

    [Fact]
    public async Task A_section_opened_as_soon_as_it_was_made_stays_open_once_the_server_names_it()
    {
        var (presenter, api) = Seeded();
        presenter.AddSection("Later", "p1");
        presenter.Select(ViewSelection.OfSection(IdOf(presenter, SidebarKind.Section, "Later")));

        api.Next = FakeApi.Naming(
            new Dictionary<string, string> { ["section_add"] = "s1" },
            Json.Change("sections", "s1", """{"id":"s1","name":"Later","project_id":"p1","section_order":1}"""));
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.OfSection("s1"), presenter.Selection);
        Assert.Equal(SidebarKeys.For(SidebarKind.Section, "s1"), presenter.SelectedKey);
    }

    [Fact]
    public async Task Opened_from_Favourites_it_stays_on_the_Favourites_row()
    {
        // The project is two rows, and the one it was opened from is the one to come back to.
        var (presenter, api) = Seeded();
        presenter.AddProject("Errands");
        var made = IdOf(presenter, SidebarKind.Project, "Errands");
        presenter.ToggleProjectFavorite(made);
        presenter.SelectByKey(SidebarKeys.Favourite(SidebarKind.Project, made));

        api.Next = FakeApi.Naming(
            new Dictionary<string, string> { ["project_add"] = "p2" },
            Json.Change("projects", "p2", """{"id":"p2","name":"Errands","child_order":2,"is_favorite":true}"""));
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.OfProject("p2"), presenter.Selection);
        Assert.Equal(SidebarKeys.Favourite(SidebarKind.Project, "p2"), presenter.SelectedKey);
    }
}
