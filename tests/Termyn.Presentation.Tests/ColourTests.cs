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
