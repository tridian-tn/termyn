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

    private static MainPresenter NewPresenter(InMemorySnapshotStore store, FakeApi? api = null)
    {
        var engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
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
    public void A_label_the_account_no_longer_holds_reads_as_nothing_either()
    {
        // A label is selected by name rather than by id, so nothing about the selection goes stale
        // on its own when one is deleted — which made this the one kind of view that would have gone
        // on naming something that wasn't there.
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.OfLabel("deleted"));

        Assert.Empty(presenter.Breadcrumbs);
    }

    [Fact]
    public void A_label_is_found_whatever_case_it_is_asked_for_in()
    {
        // Todoist matches labels without regard to case, and so does everything else here.
        var presenter = NewPresenter(Store());

        presenter.Select(ViewSelection.OfLabel("FollowUp"));

        Assert.Equal("FollowUp", Reads(presenter));
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

    // ---- While searching ---------------------------------------------------------------------------

    [Fact]
    public void Searching_reads_as_search_results_rather_than_the_list_that_was_open()
    {
        // The results are drawn from the whole account, so a path naming the list that was open
        // would say they came from it.
        var presenter = NewPresenter(Store());
        presenter.Select(ViewSelection.OfProject("inner"));

        presenter.Search("task");

        Assert.Equal("Search results", Reads(presenter));
    }

    [Fact]
    public void Search_results_are_a_single_step_that_leads_nowhere()
    {
        // There's nothing above the results to go back up to, and the step itself is where you are.
        var presenter = NewPresenter(Store());
        presenter.Select(ViewSelection.OfSection("s1"));

        presenter.Search("task");

        Assert.Null(Assert.Single(presenter.Breadcrumbs).Target);
    }

    [Fact]
    public void Clearing_the_search_puts_the_path_back()
    {
        var presenter = NewPresenter(Store());
        presenter.Select(ViewSelection.OfProject("inner"));
        presenter.Search("task");

        presenter.Search(string.Empty);

        Assert.Equal("Work / Chores", Reads(presenter));
    }

    [Fact]
    public void Moving_to_another_list_while_searching_keeps_the_results_heading()
    {
        // The search box still has its text, so the rows are still the results — and the path has
        // to say so however the view underneath them was rebuilt.
        var presenter = NewPresenter(Store());
        presenter.Select(ViewSelection.OfProject("inner"));
        presenter.Search("task");

        presenter.Select(ViewSelection.Of(SmartView.Today));
        Assert.Equal("Search results", Reads(presenter));

        presenter.Search(string.Empty);
        Assert.Equal("Today", Reads(presenter));
    }

    [Fact]
    public void A_search_of_nothing_but_spaces_is_not_a_search()
    {
        // The outline shows the list that was open when only whitespace has been typed, and the
        // path has to agree with the rows beneath it.
        var presenter = NewPresenter(Store());
        presenter.Select(ViewSelection.OfProject("inner"));

        presenter.Search("   ");

        Assert.Equal("Work / Chores", Reads(presenter));
    }

    [Fact]
    public void The_path_and_the_rows_agree_on_whether_a_search_is_on()
    {
        // One predicate decides both, and this is what that buys: never a heading naming the list
        // that was open over rows from the whole account, nor the other way round.
        var store = Store();
        store.PutResource("items", "i2", """{"id":"i2","content":"Another task","project_id":"outer","child_order":1}""");
        var presenter = NewPresenter(store);
        presenter.Select(ViewSelection.OfProject("inner"));

        presenter.Search("   ");
        Assert.Equal("Work / Chores", Reads(presenter));
        Assert.Equal(["i1"], presenter.Rows.Select(r => r.Id));

        presenter.Search("task");
        Assert.Equal("Search results", Reads(presenter));
        Assert.Contains("i2", presenter.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task A_sync_landing_mid_search_keeps_the_heading_and_the_path_beneath_it_current()
    {
        // The sync loop republishes from its own thread, and it has to leave the heading alone
        // while still taking in what changed — the list that was open, here, being renamed.
        var api = new FakeApi
        {
            Response = new SyncResponse
            {
                SyncToken = "s1",
                Changes = [Json.Change("projects", "inner", """{"id":"inner","name":"Errands","parent_id":"outer","child_order":1}""")],
            },
        };
        var presenter = NewPresenter(Store(), api);
        presenter.Select(ViewSelection.OfProject("inner"));
        presenter.Search("task");

        await presenter.SyncAsync();
        Assert.Equal("Search results", Reads(presenter));

        presenter.Search(string.Empty);
        Assert.Equal("Work / Errands", Reads(presenter));
    }
}
