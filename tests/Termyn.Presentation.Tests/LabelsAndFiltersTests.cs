using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>The sidebar's label and filter rows, and what selecting one shows.</summary>
public class LabelsAndFiltersTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- Sidebar -----------------------------------------------------------------------------------

    [Fact]
    public void Labels_are_listed_under_their_own_header()
    {
        var presenter = NewPresenter(Seeded());

        var headers = presenter.Sidebar.Where(n => n.Kind == SidebarKind.Header).Select(n => n.Label).ToList();
        Assert.Equal(["Favourites", "Projects", "Labels", "Filters"], headers);
    }

    [Fact]
    public void Labels_are_listed_in_their_own_order()
    {
        var presenter = NewPresenter(Seeded());

        var labels = presenter.Sidebar
            .SkipWhile(n => n.Label != "Labels")
            .Skip(1)
            .TakeWhile(n => n.Kind != SidebarKind.Header)
            .Select(n => n.Label);

        Assert.Equal(["home", "errand"], labels); // item_order 1 then 2, not alphabetical
    }

    [Fact]
    public void A_label_row_counts_the_tasks_wearing_it()
    {
        var presenter = NewPresenter(Seeded());

        Assert.Equal(2, presenter.Sidebar.First(n => n is { Kind: SidebarKind.Label, Label: "home" }).Count);
        Assert.Equal(1, presenter.Sidebar.First(n => n is { Kind: SidebarKind.Label, Label: "errand" }).Count);
    }

    [Fact]
    public void Favourites_group_projects_then_labels_then_filters()
    {
        // Three separate order fields; interleaving them would have nothing to sort by.
        var presenter = NewPresenter(Seeded());

        var favourites = presenter.Sidebar
            .SkipWhile(n => n.Label != "Favourites")
            .Skip(1)
            .TakeWhile(n => n.Kind != SidebarKind.Header)
            .ToList();

        Assert.Equal([SidebarKind.Project, SidebarKind.Label, SidebarKind.Filter], favourites.Select(n => n.Kind));
        Assert.Equal(["Work", "home", "Hot"], favourites.Select(n => n.Label));
    }

    [Fact]
    public void A_favourite_label_is_keyed_apart_from_its_row_in_the_list()
    {
        // Both rows are the same label; clicking one must not select the other.
        var presenter = NewPresenter(Seeded());

        var rows = presenter.Sidebar.Where(n => n is { Kind: SidebarKind.Label, Label: "home" }).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(n => n.Key).Distinct().Count());
    }

    [Fact]
    public void Rows_of_different_kinds_sharing_an_id_are_still_separate_rows()
    {
        // Todoist ids are only unique within a resource type, so a project and a filter can both
        // be "7". Without the kind in the key they would be the same row to the tree.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "7", """{"id":"7","name":"Work","is_favorite":true}""");
        store.PutResource("sections", "7", """{"id":"7","name":"Admin","project_id":"7"}""");
        store.PutResource("filters", "7", """{"id":"7","name":"Hot","query":"today","is_favorite":true}""");
        var presenter = NewPresenter(store);

        var keys = presenter.Sidebar.Select(n => n.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Selecting_a_filter_finds_its_own_row_not_a_project_with_the_same_id()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "7", """{"id":"7","name":"Work"}""");
        store.PutResource("filters", "7", """{"id":"7","name":"Hot","query":"today"}""");
        var presenter = NewPresenter(store);

        presenter.Select(ViewSelection.OfFilter("7"));

        var row = presenter.Sidebar.Single(n => n.Key == presenter.Selection.Key);
        Assert.Equal(SidebarKind.Filter, row.Kind);
    }

    [Fact]
    public void A_label_carries_its_name_as_its_id()
    {
        // Tasks refer to labels by name, so that is what a selection has to hold.
        var presenter = NewPresenter(Seeded());

        Assert.Equal("home", presenter.Sidebar.First(n => n is { Kind: SidebarKind.Label, Label: "home" }).Id);
    }

    [Fact]
    public void An_account_with_no_labels_or_filters_has_no_headers_for_them()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        var presenter = NewPresenter(store);

        var headers = presenter.Sidebar.Where(n => n.Kind == SidebarKind.Header).Select(n => n.Label);
        Assert.Equal(["Projects"], headers);
    }

    // ---- Selecting ---------------------------------------------------------------------------------

    [Fact]
    public void Selecting_a_label_shows_the_tasks_wearing_it()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.OfLabel("home"));

        Assert.Equal(["Chores", "Shopping"], presenter.Rows.Select(r => r.Content).Order());
    }

    [Fact]
    public void A_label_selection_ignores_case()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.OfLabel("HOME"));

        Assert.Equal(2, presenter.Rows.Count);
    }

    [Fact]
    public void Selecting_a_filter_runs_its_query()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.OfFilter("f1")); // "@home & p1"

        Assert.Equal(["Chores"], presenter.Rows.Select(r => r.Content));
        Assert.Null(presenter.UnsupportedFilter);
    }

    [Fact]
    public void A_filter_Termyn_cannot_read_shows_nothing_and_says_so()
    {
        // Showing every task would read as a filter that ran and matched broadly.
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.OfFilter("f2")); // "assigned to: me"

        Assert.Empty(presenter.Rows);
        Assert.Equal("assigned to: me", presenter.UnsupportedFilter);
    }

    [Fact]
    public void Moving_off_an_unsupported_filter_clears_the_warning()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfFilter("f2"));

        presenter.Select(ViewSelection.Of(SmartView.All));

        Assert.Null(presenter.UnsupportedFilter);
    }

    [Fact]
    public void A_filter_that_no_longer_exists_shows_nothing()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.OfFilter("gone"));

        Assert.Empty(presenter.Rows);
    }

    [Fact]
    public void A_filter_naming_a_project_resolves_it_by_name()
    {
        var presenter = NewPresenter(Seeded());

        presenter.Select(ViewSelection.OfFilter("f3")); // "#Work"

        Assert.Equal(["Chores", "Report"], presenter.Rows.Select(r => r.Content).Order());
    }

    // ---- Label intents -----------------------------------------------------------------------------

    [Fact]
    public void Setting_the_labels_on_a_task_shows_them_straight_away()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.Of(SmartView.All));

        presenter.SetLabels("i3", ["home"]);

        Assert.Contains("home", presenter.Rows.Single(r => r.Id == "i3").Labels);
    }

    [Fact]
    public void Adding_a_label_that_already_exists_reuses_it()
    {
        var presenter = NewPresenter(Seeded());
        var before = presenter.Labels.Count;

        Assert.Equal("home", presenter.AddLabel("HOME"));
        Assert.Equal(before, presenter.Labels.Count);
    }

    [Fact]
    public void Deleting_the_label_being_viewed_falls_back_to_the_default_view()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfLabel("home"));

        presenter.DeleteLabel("l1");

        Assert.Equal(SmartView.Today, presenter.Selection.View);
    }

    private static InMemorySnapshotStore Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1,"is_favorite":true}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1,"is_favorite":true}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"errand","item_order":2}""");
        store.PutResource("filters", "f1", """{"id":"f1","name":"Hot","query":"@home & p1","item_order":1,"is_favorite":true}""");
        store.PutResource("filters", "f2", """{"id":"f2","name":"Mine","query":"assigned to: me","item_order":2}""");
        store.PutResource("filters", "f3", """{"id":"f3","name":"Job","query":"#Work","item_order":3}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Chores","project_id":"p1","child_order":1,"priority":4,"labels":["home"]}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Shopping","child_order":2,"labels":["home","errand"]}""");
        store.PutResource("items", "i3", """{"id":"i3","content":"Report","project_id":"p1","child_order":3}""");
        return store;
    }

    private static MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
