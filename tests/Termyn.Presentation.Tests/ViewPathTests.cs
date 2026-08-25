using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The line above the outline saying which list you are on, and the way back up it offers.
/// </summary>
public class ViewPathTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    /// <summary>
    /// A tree with a project inside a project, a section in the inner one, and a label and a filter
    /// alongside — one of each thing a path can be built from.
    /// </summary>
    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "outer", """{"id":"outer","name":"Work","child_order":1}""");
        store.PutResource("projects", "inner", """{"id":"inner","name":"Chores","parent_id":"outer","child_order":1}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"inner"}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup"}""");
        store.PutResource("filters", "f1", """{"id":"f1","name":"Overdue p1","query":"overdue & p1"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"A task","project_id":"inner","child_order":1}""");
        return store;
    }

    private static MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
    }

    /// <summary>What the line reads, with the separator the header draws between steps.</summary>
    private static string Reads(MainPresenter presenter)
        => string.Join(" / ", presenter.Breadcrumbs.Select(c => c.Label));

    [Fact]
    public void A_built_in_view_is_a_single_step()
    {
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.Of(SmartView.Today));

        Assert.Equal("Today", Reads(presenter));
    }

    [Fact]
    public void A_project_inside_a_project_names_the_one_holding_it()
    {
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.OfProject("inner"));

        Assert.Equal("Work / Chores", Reads(presenter));
    }

    [Fact]
    public void A_section_names_the_projects_above_it_as_well()
    {
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.OfSection("s1"));

        Assert.Equal("Work / Chores / Admin", Reads(presenter));
    }

    [Fact]
    public void A_label_and_a_filter_have_nothing_above_them()
    {
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.OfLabel("followup"));
        Assert.Equal("followup", Reads(presenter));

        presenter.Select(ViewSelection.OfFilter("f1"));
        Assert.Equal("Overdue p1", Reads(presenter));
    }

    // ---- The way back up ---------------------------------------------------------------------------

    [Fact]
    public void Every_step_but_the_last_goes_somewhere()
    {
        // What makes the line a way back up rather than a caption. The last step is where you are
        // already, and offering it as somewhere to go would be offering to do nothing.
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.OfSection("s1"));

        Assert.Equal(ViewSelection.OfProject("outer"), presenter.Breadcrumbs[0].Target);
        Assert.Equal(ViewSelection.OfProject("inner"), presenter.Breadcrumbs[1].Target);
        Assert.Null(presenter.Breadcrumbs[2].Target);
    }

    [Fact]
    public void A_single_step_leads_nowhere_either()
    {
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.Of(SmartView.Today));

        Assert.Null(Assert.Single(presenter.Breadcrumbs).Target);
    }

    [Fact]
    public void Following_a_step_takes_the_outline_there()
    {
        // The point of the whole thing: the target a step carries is a selection the presenter
        // accepts, and following it puts you on that list with its own path.
        var presenter = NewPresenter(Store());
        presenter.Select(ViewSelection.OfSection("s1"));

        presenter.Select(presenter.Breadcrumbs[0].Target!);

        Assert.Equal("Work", Reads(presenter));
    }

    // ---- Written as a line -------------------------------------------------------------------------

    [Fact]
    public void The_line_joins_the_steps_and_marks_only_the_ones_that_lead_somewhere()
    {
        var presenter = NewPresenter(Store());
        presenter.Select(ViewSelection.OfSection("s1"));

        var line = ViewPath.Line(presenter.Breadcrumbs, " / ");

        Assert.Equal("Work / Chores / Admin", line.Text);
        Assert.Equal(2, line.Links.Count);
        Assert.Equal("Work", line.Text.Substring(line.Links[0].Start, line.Links[0].Length));
        Assert.Equal("Chores", line.Text.Substring(line.Links[1].Start, line.Links[1].Length));
    }

    [Fact]
    public void Two_steps_of_the_same_name_are_marked_one_each()
    {
        // A project inside a project of the same name is ordinary enough, and finding each step by
        // its text would mark the first of them twice and leave the second unmarked.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "outer", """{"id":"outer","name":"Admin"}""");
        store.PutResource("projects", "middle", """{"id":"middle","name":"Admin","parent_id":"outer"}""");
        store.PutResource("projects", "inner", """{"id":"inner","name":"Notes","parent_id":"middle"}""");
        var presenter = NewPresenter(store);
        presenter.Select(ViewSelection.OfProject("inner"));

        var line = ViewPath.Line(presenter.Breadcrumbs, " / ");

        Assert.Equal("Admin / Admin / Notes", line.Text);
        Assert.Equal(0, line.Links[0].Start);
        Assert.Equal(8, line.Links[1].Start);
        Assert.Equal(ViewSelection.OfProject("outer"), line.Links[0].Target);
        Assert.Equal(ViewSelection.OfProject("middle"), line.Links[1].Target);
    }

    [Fact]
    public void A_line_with_nothing_on_it_is_empty_and_leads_nowhere()
    {
        var line = ViewPath.Line([], " / ");

        Assert.Equal(string.Empty, line.Text);
        Assert.Empty(line.Links);
    }

    // ---- When the model can't answer ---------------------------------------------------------------

    [Fact]
    public void A_view_of_something_that_is_no_longer_there_reads_as_nothing()
    {
        // A project deleted by a sync while it was open. Better an empty line than a step naming
        // something the account doesn't hold, which would go nowhere when followed.
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.OfProject("gone"));

        Assert.Empty(presenter.Breadcrumbs);
    }

    [Fact]
    public void A_project_that_is_its_own_ancestor_does_not_walk_for_ever()
    {
        // Not reachable through the UI, but it arrives over the wire and the walk up is a loop.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "a", """{"id":"a","name":"A","parent_id":"b"}""");
        store.PutResource("projects", "b", """{"id":"b","name":"B","parent_id":"a"}""");
        var presenter = NewPresenter(store);

        presenter.Select(ViewSelection.OfProject("a"));

        Assert.Equal("B / A", Reads(presenter));
    }
}
