using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The colours Todoist gives a project, as far as the rows are concerned.
/// </summary>
/// <remarks>
/// The row carries the colour rather than the name of it, so nothing drawing a row has to know what
/// Todoist's names mean. The labels on a row are a different matter: they join by name, and the
/// window looks each one up as it draws.
/// </remarks>
public class ColourTests
{
    private static readonly DateOnly Today = new(2026, 9, 18);

    private static MainPresenter Presenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), new FixedClock(Today));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }

    private static InMemorySnapshotStore Seeded(string projectJson)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", projectJson);
        store.PutResource("items", "t1", """{"id":"t1","content":"Plan the week","project_id":"p1","child_order":1}""");
        return store;
    }

    [Fact]
    public void A_row_carries_the_colour_of_the_project_it_is_in()
    {
        var presenter = Presenter(Seeded("""{"id":"p1","name":"Work","color":"berry_red"}"""));

        Assert.Equal(TodoistPalette.Of("berry_red"), presenter.Rows.Single().ProjectColour);
    }

    [Fact]
    public void A_project_with_no_colour_of_its_own_gets_todoists_default()
    {
        // Charcoal, which is what Todoist shows for one that has never been given a colour.
        var presenter = Presenter(Seeded("""{"id":"p1","name":"Work"}"""));

        Assert.Equal(TodoistPalette.Charcoal, presenter.Rows.Single().ProjectColour);
    }

    [Fact]
    public void A_task_in_no_project_at_all_has_no_colour_to_draw()
    {
        // Not charcoal: there's no project, so there's no dot, and a row that drew one would be
        // saying something about a project it hasn't got.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "t1", """{"id":"t1","content":"Plan the week","child_order":1}""");

        var row = Presenter(store).Rows.Single();

        Assert.Equal(string.Empty, row.Project);
        Assert.Null(row.ProjectColour);
    }

    // ---- The sidebar -----------------------------------------------------------------------------

    [Fact]
    public void A_project_a_label_and_a_filter_each_carry_their_colour_into_the_sidebar()
    {
        var store = Seeded("""{"id":"p1","name":"Work","color":"berry_red"}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup","color":"teal"}""");
        store.PutResource("filters", "f1", """{"id":"f1","name":"Overdue","query":"overdue","color":"grape"}""");

        var sidebar = Presenter(store).Sidebar;

        Assert.Equal(TodoistPalette.Of("berry_red"), sidebar.Single(n => n.Kind == SidebarKind.Project).Colour);
        Assert.Equal(TodoistPalette.Of("teal"), sidebar.Single(n => n.Kind == SidebarKind.Label).Colour);
        Assert.Equal(TodoistPalette.Of("grape"), sidebar.Single(n => n.Kind == SidebarKind.Filter).Colour);
    }

    [Fact]
    public void A_favourites_copy_of_a_row_is_the_same_colour_as_the_row_itself()
    {
        // The two are separate rows, keyed apart so clicking one doesn't select the other, and a
        // colour on one and not the other would read as two different projects.
        var store = Seeded("""{"id":"p1","name":"Work","color":"berry_red","is_favorite":true}""");

        var projects = Presenter(store).Sidebar.Where(n => n.Kind == SidebarKind.Project).ToList();

        Assert.Equal(2, projects.Count);
        Assert.All(projects, p => Assert.Equal(TodoistPalette.Of("berry_red"), p.Colour));
    }

    [Fact]
    public void Termyns_own_rows_have_no_colour_to_draw()
    {
        // Smart views, sections and the headings between them are Termyn's furniture rather than
        // the account's. A dot beside them would be inventing something Todoist never said.
        var store = Seeded("""{"id":"p1","name":"Work","color":"berry_red"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Reports","project_id":"p1"}""");

        var sidebar = Presenter(store).Sidebar;

        Assert.All(
            sidebar.Where(n => n.Kind is SidebarKind.SmartView or SidebarKind.Section or SidebarKind.Header),
            n => Assert.Null(n.Colour));
    }

    [Fact]
    public void The_labels_a_window_draws_carry_their_own_colours()
    {
        var store = Seeded("""{"id":"p1","name":"Work"}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup","color":"teal"}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"waiting","color":"grape"}""");

        var labels = Presenter(store).Labels;

        Assert.Equal(TodoistPalette.Of("teal"), TodoistPalette.Of(labels.Single(l => l.Name == "followup").Color));
        Assert.Equal(TodoistPalette.Of("grape"), TodoistPalette.Of(labels.Single(l => l.Name == "waiting").Color));
    }
}
