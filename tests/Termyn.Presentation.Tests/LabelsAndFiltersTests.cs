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
    public void Two_favourite_labels_of_the_same_name_are_listed_once()
    {
        // Nothing stops two labels sharing a name — renaming one onto another is enough. They are
        // one view, and two rows with one key are two rows the tree can't tell apart.
        var store = new InMemorySnapshotStore();
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1,"is_favorite":true}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"home","item_order":2,"is_favorite":true}""");
        var presenter = NewPresenter(store);

        var keys = presenter.Sidebar.Select(n => n.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Single(presenter.Sidebar, n => n.Key == SidebarKeys.Favourite(SidebarKind.Label, "home"));
    }

    [Fact]
    public void A_label_row_is_starred_when_any_label_of_that_name_is()
    {
        // The row stands for the name, so a star that depended on which duplicate happened to be
        // enumerated first would contradict the row's own copy under Favourites.
        var store = new InMemorySnapshotStore();
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"home","item_order":2,"is_favorite":true}""");
        var presenter = NewPresenter(store);

        Assert.True(presenter.Sidebar.Single(n => n.Key == SidebarKeys.For(SidebarKind.Label, "home")).IsFavorite);
        Assert.Single(presenter.Sidebar, n => n.Key == SidebarKeys.Favourite(SidebarKind.Label, "home"));
    }

    [Fact]
    public void An_operation_on_a_label_row_reaches_every_label_of_that_name()
    {
        // Renaming one of them would leave the other still carrying the old name, so the row the
        // user thought they had just renamed would still be there.
        var store = new InMemorySnapshotStore();
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"home","item_order":2}""");
        var presenter = NewPresenter(store);

        presenter.RenameLabel("home", "household");

        Assert.Equal(["household", "household"], presenter.Labels.Select(l => l.Name));
        Assert.DoesNotContain(presenter.Sidebar, n => n.Label == "home");
    }

    [Fact]
    public void Unfavouriting_a_label_row_clears_the_star_on_all_of_them()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"home","item_order":2,"is_favorite":true}""");
        var presenter = NewPresenter(store);

        // The row shows a star, so the first toggle has to be the one that takes it away.
        presenter.ToggleLabelFavorite("home");

        Assert.All(presenter.Labels, l => Assert.False(l.IsFavorite));
        Assert.DoesNotContain(presenter.Sidebar, n => n.Label == "Favourites");
    }

    [Fact]
    public void Deleting_a_label_row_removes_every_label_of_that_name()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"home","item_order":2}""");
        var presenter = NewPresenter(store);

        presenter.DeleteLabel("home");

        Assert.Empty(presenter.Labels);
    }

    [Fact]
    public void An_operation_on_a_label_that_is_not_there_does_nothing()
    {
        var presenter = NewPresenter(Seeded());

        presenter.RenameLabel("ghost", "x");
        presenter.ToggleLabelFavorite("ghost");
        presenter.DeleteLabel("ghost");

        Assert.DoesNotContain("pending", presenter.Status);
    }

    [Fact]
    public void A_blank_label_name_is_not_added()
    {
        // The server rejects it, and a rejected command retries to its ceiling and then sits in the
        // outbox as a failure the user can't act on.
        var presenter = NewPresenter(Seeded());
        var before = presenter.Labels.Count;

        Assert.Null(presenter.AddLabel("   "));
        Assert.Equal(before, presenter.Labels.Count);
        Assert.DoesNotContain("pending", presenter.Status);
    }

    [Fact]
    public void Renaming_the_label_being_viewed_follows_it_to_the_new_name()
    {
        // The selection holds a label by name, so a rename moves the view with it — otherwise the
        // sidebar highlights nothing and the outline empties on the next sync.
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfLabel("home"));

        presenter.RenameLabel("home", "household");

        Assert.Equal("household", presenter.Selection.LabelName);
        Assert.Contains(presenter.Sidebar, n => n.Key == presenter.Selection.Key);
    }

    [Fact]
    public void Renaming_a_label_leaves_a_different_view_alone()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfLabel("errand"));

        presenter.RenameLabel("home", "household");

        Assert.Equal("errand", presenter.Selection.LabelName);
    }

    [Fact]
    public void An_unreadable_filter_is_reported_in_something_a_single_line_can_hold()
    {
        // The query comes off the account, so it can be any length and carry newlines that would
        // break the line it is shown on.
        var store = new InMemorySnapshotStore();
        store.PutResource("filters", "f1", $$"""{"id":"f1","name":"Big","query":"{{new string('x', 5000)}}"}""");
        store.PutResource("filters", "f2", """{"id":"f2","name":"Blank","query":""}""");
        var presenter = NewPresenter(store);

        presenter.Select(ViewSelection.OfFilter("f1"));
        Assert.True(presenter.UnsupportedFilter!.Length <= 201);

        presenter.Select(ViewSelection.OfFilter("f2"));
        Assert.NotEmpty(presenter.UnsupportedFilter!);
    }

    [Fact]
    public void Selecting_a_readable_filter_clears_a_previous_warning()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfFilter("f2")); // unreadable

        presenter.Select(ViewSelection.OfFilter("f1")); // readable

        Assert.Null(presenter.UnsupportedFilter);
    }

    [Fact]
    public void Filters_are_listed_in_their_own_order()
    {
        var presenter = NewPresenter(Seeded());

        var filters = presenter.Sidebar
            .SkipWhile(n => n.Label != "Filters")
            .Skip(1)
            .TakeWhile(n => n.Kind != SidebarKind.Header)
            .Select(n => n.Label);

        Assert.Equal(["Hot", "Mine", "Job"], filters); // item_order, not alphabetical
    }

    [Fact]
    public void A_label_row_counts_tasks_however_they_spell_the_label()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"One","child_order":1,"labels":["Home"]}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Two","child_order":2,"labels":["home"]}""");
        var presenter = NewPresenter(store);

        var row = presenter.Sidebar.Single(n => n.Key == SidebarKeys.For(SidebarKind.Label, "home"));
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public void Deleting_the_label_being_viewed_falls_back_to_the_default_view()
    {
        var presenter = NewPresenter(Seeded());
        presenter.Select(ViewSelection.OfLabel("home"));

        presenter.DeleteLabel("home");

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
