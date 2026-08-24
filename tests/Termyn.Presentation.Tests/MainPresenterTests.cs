using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

public class MainPresenterTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public async Task Renders_active_tasks_ordered_by_child_order_with_project_names()
    {
        var api = new FakeApi
        {
            Response = new SyncResponse
            {
                SyncToken = "s1",
                Changes =
                [
                    Json.Change("projects", "p1", """{"id":"p1","name":"Work"}"""),
                    Json.Change("items", "done", """{"id":"done","content":"Done","checked":true,"child_order":0}"""),
                    Json.Change("items", "b", """{"id":"b","content":"Second","project_id":"p1","priority":1,"child_order":2}"""),
                    Json.Change("items", "a", """{"id":"a","content":"First","project_id":"p1","priority":4,"child_order":1}"""),
                ],
            },
        };
        var presenter = NewPresenter(api, new InMemorySnapshotStore());

        await presenter.LoadAsync();

        Assert.Equal(new[] { "First", "Second" }, presenter.Rows.Select(r => r.Content).ToArray());
        Assert.Equal("Work", presenter.Rows.Single(r => r.Content == "Second").Project);
        Assert.Equal(Priority.P1, presenter.Rows.Single(r => r.Content == "First").Priority); // API 4 -> P1
        Assert.False(presenter.IsOffline);
    }

    [Fact]
    public async Task Publishes_cached_rows_before_syncing()
    {
        var presenter = NewPresenter(new FakeApi { Response = new SyncResponse { SyncToken = "s1" } }, SeededStore());

        var publishes = new List<int>();
        presenter.RowsChanged += () => publishes.Add(presenter.Rows.Count);
        await presenter.LoadAsync();

        // Two publishes: the cached view first, then the reconciled one.
        Assert.Equal(2, publishes.Count);
        Assert.Equal(1, publishes[0]);
    }

    [Fact]
    public async Task Losing_the_network_keeps_the_cached_rows_and_reports_offline()
    {
        var presenter = NewPresenter(new FakeApi { Throw = new TodoistNetworkException("offline") }, SeededStore());

        await presenter.LoadAsync();

        Assert.Equal("Cached task", presenter.Rows.Single().Content);
        Assert.True(presenter.IsOffline);
        Assert.Contains("offline", presenter.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recovering_the_network_clears_the_offline_status()
    {
        var api = new FakeApi { Throw = new TodoistNetworkException("offline") };
        var presenter = NewPresenter(api, SeededStore());
        await presenter.LoadAsync();
        Assert.True(presenter.IsOffline);

        api.Throw = null;
        api.Response = new SyncResponse { SyncToken = "s1" };
        await presenter.SyncAsync();

        Assert.False(presenter.IsOffline);
        Assert.DoesNotContain("offline", presenter.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clears_token_and_rethrows_when_rejected()
    {
        var secrets = new FakeSecrets { Stored = "tok" };
        var engine = new SyncEngine(new FakeApi { Throw = new TodoistAuthException("no") }, new InMemorySnapshotStore(), secrets);
        var presenter = new MainPresenter(engine, Parser());

        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.LoadAsync());
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public void Status_reports_the_pending_count()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Rename("i1", "Renamed");

        Assert.Equal("1 task · Not synced yet · 1 pending", presenter.Status);
    }

    // ---- Capture -------------------------------------------------------------------------------

    [Fact]
    public async Task Capture_uses_the_server_parse_when_online()
    {
        var api = new FakeApi
        {
            QuickAdd = text => Json.Change("items", "new", $$"""{"id":"new","content":"{{text}}"}"""),
        };
        var presenter = NewPresenter(api, new InMemorySnapshotStore());

        await presenter.CaptureAsync("Email report tomorrow p1");

        Assert.Equal(1, api.QuickAddCalls);
        Assert.Equal("Email report tomorrow p1", presenter.Rows.Single().Content);
        Assert.False(presenter.IsOffline);
    }

    [Fact]
    public async Task Capture_falls_back_to_the_local_grammar_when_offline()
    {
        var presenter = NewPresenter(new FakeApi(), new InMemorySnapshotStore()); // QuickAdd unset = unreachable

        await presenter.CaptureAsync("Email report tomorrow p1 @followup");

        var row = presenter.Rows.Single();
        Assert.Equal("Email report", row.Content);
        Assert.Equal(Priority.P1, row.Priority);
        Assert.Equal("1 Aug", row.Due);
        Assert.True(presenter.IsOffline);
    }

    [Fact]
    public async Task Capture_of_only_tokens_keeps_the_raw_text_rather_than_creating_a_blank_task()
    {
        var presenter = NewPresenter(new FakeApi(), new InMemorySnapshotStore());

        await presenter.CaptureAsync("#Work p1");

        Assert.Equal("#Work p1", presenter.Rows.Single().Content);
    }

    [Fact]
    public async Task Capture_ignores_blank_text()
    {
        var api = new FakeApi();
        var presenter = NewPresenter(api, new InMemorySnapshotStore());

        await presenter.CaptureAsync("   ");

        Assert.Empty(presenter.Rows);
        Assert.Equal(0, api.QuickAddCalls);
    }

    [Fact]
    public void Preview_reports_unresolved_project_and_section_names()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Reports","project_id":"p1"}""");
        var presenter = NewPresenter(new FakeApi(), store);

        var known = presenter.Preview("Task #Work /Reports");
        Assert.True(known.ProjectResolved);
        Assert.True(known.SectionResolved);

        var unknown = presenter.Preview("Task #Nope /Missing");
        Assert.False(unknown.ProjectResolved);
        Assert.False(unknown.SectionResolved);
    }

    [Fact]
    public void A_section_name_shared_by_two_projects_needs_the_project_to_disambiguate()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Home"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Admin","project_id":"p1"}""");
        store.PutResource("sections", "s2", """{"id":"s2","name":"Admin","project_id":"p2"}""");
        var presenter = NewPresenter(new FakeApi(), store);

        // Ambiguous on its own: filing it under either project's section would be a guess.
        Assert.False(presenter.Preview("Task /Admin").SectionResolved);

        // Unambiguous once the project is named.
        Assert.True(presenter.Preview("Task #Home /Admin").SectionResolved);
    }

    [Fact]
    public async Task A_section_named_under_an_unknown_project_is_not_resolved_account_wide()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Reports","project_id":"p1"}""");
        var presenter = NewPresenter(new FakeApi(), store);

        // The project was named and we don't know it, so we don't know where this task belongs.
        Assert.False(presenter.Preview("Task #Nope /Reports").SectionResolved);

        await presenter.CaptureAsync("Task #Nope /Reports");
        Assert.Equal(string.Empty, presenter.Rows.Single().Project);
    }

    [Fact]
    public async Task A_bare_section_files_the_task_into_that_sections_project()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("sections", "s1", """{"id":"s1","name":"Reports","project_id":"p1"}""");
        var presenter = NewPresenter(new FakeApi(), store);

        await presenter.CaptureAsync("Task /Reports");

        // A section id without its project would be rejected, so the project comes along with it.
        Assert.Equal("Work", presenter.Rows.Single().Project);
    }

    [Fact]
    public void An_ambiguous_project_name_is_treated_as_unresolved()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Work"}""");
        var presenter = NewPresenter(new FakeApi(), store);

        Assert.False(presenter.Preview("Task #Work").ProjectResolved);
    }

    [Fact]
    public void Preview_shows_the_text_the_task_would_actually_be_created_with()
    {
        var presenter = NewPresenter(new FakeApi(), new InMemorySnapshotStore());

        // Every word was a token, so the raw text is kept rather than creating a blank task.
        Assert.Equal("#Work p1", presenter.Preview("#Work p1").Parse.Content);
    }

    [Fact]
    public void Preview_flags_what_the_local_parser_cannot_handle()
    {
        var presenter = NewPresenter(new FakeApi(), new InMemorySnapshotStore());

        var preview = presenter.Preview("Water plants every day #Home");

        Assert.Equal("Home", preview.Parse.ProjectName);
        Assert.NotEmpty(preview.Parse.Unsupported); // recurrence is not handled offline
    }

    [Fact]
    public async Task An_offline_capture_resolves_the_project_by_name()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        var presenter = NewPresenter(new FakeApi(), store);

        await presenter.CaptureAsync("Email report #Work");

        Assert.Equal("Work", presenter.Rows.Single().Project);
    }

    // ---- Intents -------------------------------------------------------------------------------

    [Fact]
    public void Completing_a_task_removes_it_from_the_list_and_can_be_undone()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Complete("i1");
        Assert.Empty(presenter.Rows);
        Assert.True(presenter.CanUndo);

        Assert.True(presenter.Undo());
        Assert.Equal("Cached task", presenter.Rows.Single().Content);
    }

    [Fact]
    public void Deleting_a_task_removes_it_and_can_be_undone()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Delete("i1");
        Assert.Empty(presenter.Rows);

        Assert.True(presenter.Undo());
        Assert.Equal("Cached task", presenter.Rows.Single().Content);
    }

    [Fact]
    public void Renaming_priority_and_due_date_show_up_immediately()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Rename("i1", "Renamed");
        presenter.SetPriority("i1", Priority.P2);
        presenter.SetDue("i1", Today);

        var row = presenter.Rows.Single();
        Assert.Equal("Renamed", row.Content);
        Assert.Equal(Priority.P2, row.Priority);
        Assert.Equal("31 Jul", row.Due);
    }

    [Fact]
    public void Clearing_a_due_date_empties_the_column()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());
        presenter.SetDue("i1", Today);

        presenter.SetDue("i1", null);

        Assert.Equal(string.Empty, presenter.Rows.Single().Due);
    }

    [Fact]
    public void Search_filters_by_content_project_and_label()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");
        store.PutResource("items", "a", """{"id":"a","content":"Email report","project_id":"p1","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Buy milk","labels":["errand"],"child_order":2}""");
        var presenter = NewPresenter(new FakeApi(), store);

        presenter.Search("milk");
        Assert.Equal("Buy milk", presenter.Rows.Single().Content);

        presenter.Search("work");
        Assert.Equal("Email report", presenter.Rows.Single().Content);

        presenter.Search("errand");
        Assert.Equal("Buy milk", presenter.Rows.Single().Content);

        presenter.Search("");
        Assert.Equal(2, presenter.Rows.Count);
    }

    [Fact]
    public void Moving_a_task_reorders_the_list()
    {
        var presenter = NewPresenter(new FakeApi(), OrderedStore());

        presenter.Move("b", -1);

        Assert.Equal(new[] { "B", "A" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    [Fact]
    public void Moving_while_a_search_filter_is_active_moves_one_place_in_the_full_list()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"alpha","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"beta match","project_id":"p","child_order":2}""");
        store.PutResource("items", "c", """{"id":"c","content":"gamma","project_id":"p","child_order":3}""");
        store.PutResource("items", "d", """{"id":"d","content":"delta match","project_id":"p","child_order":4}""");
        var engine = NewEngine(new FakeApi(), store);
        var presenter = new MainPresenter(engine, Parser());
        presenter.Select(ViewSelection.Of(SmartView.All));

        presenter.Search("match");
        presenter.Move("d", -1);
        presenter.Search("");

        // One place up in the real list, not one place up among the two visible rows.
        Assert.Equal(new[] { "a", "b", "d", "c" }, presenter.Rows.Select(r => r.Id).ToArray());

        // And no two tasks ended up sharing a position.
        var orders = engine.Snapshot().Items.Select(i => i.ChildOrder).ToArray();
        Assert.Equal(orders.Length, orders.Distinct().Count());
    }

    [Fact]
    public void Rows_are_ordered_so_that_moving_one_place_on_screen_is_one_place_in_the_model()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a1", """{"id":"a1","content":"A1","project_id":"pA","child_order":1}""");
        store.PutResource("items", "a2", """{"id":"a2","content":"A2","project_id":"pA","child_order":2}""");
        store.PutResource("items", "b1", """{"id":"b1","content":"B1","project_id":"pB","child_order":1}""");
        var presenter = NewPresenter(new FakeApi(), store);

        // Projects are kept together, so a task's on-screen neighbour is a real sibling.
        Assert.Equal(new[] { "a1", "a2", "b1" }, presenter.Rows.Select(r => r.Id).ToArray());

        Assert.True(presenter.Move("a2", -1));
        Assert.Equal(new[] { "a2", "a1", "b1" }, presenter.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Subtasks_are_grouped_under_their_parent_so_a_move_is_one_place()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "t1", """{"id":"t1","content":"T1","project_id":"p","child_order":1}""");
        store.PutResource("items", "t2", """{"id":"t2","content":"T2","project_id":"p","child_order":2}""");
        store.PutResource("items", "c1", """{"id":"c1","content":"C1","project_id":"p","parent_id":"t1","child_order":1}""");
        store.PutResource("items", "c2", """{"id":"c2","content":"C2","project_id":"p","parent_id":"t1","child_order":2}""");
        var presenter = NewPresenter(new FakeApi(), store);

        // Children sit under their parent, indented.
        Assert.Equal(new[] { "t1", "c1", "c2", "t2" }, presenter.Rows.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { 0, 1, 1, 0 }, presenter.Rows.Select(r => r.Depth).ToArray());

        Assert.True(presenter.Move("c2", -1));
        Assert.Equal(new[] { "t1", "c2", "c1", "t2" }, presenter.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Siblings_sharing_a_position_are_ordered_the_way_the_engine_orders_them()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        var presenter = NewPresenter(new FakeApi(), store);

        Assert.Equal(new[] { "a", "b" }, presenter.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Syncing_while_offline_does_not_ask_the_scheduler_to_come_straight_back()
    {
        var api = new FakeApi();
        var presenter = NewPresenter(api, SeededStore());
        presenter.Rename("i1", "B"); // something queued

        api.Throw = new TodoistNetworkException("offline");

        // Asking for another round here would spin the scheduler against a dead network.
        Assert.False((await presenter.SyncAsync()).MoreQueued);
        Assert.True(presenter.IsOffline);
    }

    [Fact]
    public async Task Syncing_with_writes_still_queued_asks_for_another_round()
    {
        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var presenter = NewPresenter(api, SeededStore());
        presenter.Rename("i1", "B");

        Assert.True((await presenter.SyncAsync()).MoreQueued); // no verdict yet, so it stays queued
    }

    [Fact]
    public void Renaming_a_task_that_is_no_longer_listed_is_a_no_op()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Rename("gone", "X");

        Assert.Equal("Cached task", presenter.Rows.Single().Content);
    }

    [Fact]
    public void Moving_a_task_that_cannot_move_reports_that_nothing_changed()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        Assert.False(presenter.Move("i1", -1));
        Assert.False(presenter.Move("unknown", 1));
    }

    [Fact]
    public void Moving_past_either_end_does_nothing()
    {
        var presenter = NewPresenter(new FakeApi(), SeededStore());

        presenter.Move("i1", -1);
        presenter.Move("i1", 1);

        Assert.Equal("Cached task", presenter.Rows.Single().Content);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>
    /// A presenter showing every task. The app lands on Today, but these tests are about the rows
    /// and intents rather than the smart views, which have their own tests.
    /// </summary>
    private static MainPresenter NewPresenter(FakeApi api, InMemorySnapshotStore store)
    {
        var presenter = new MainPresenter(NewEngine(api, store), Parser());
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }

    private static SyncEngine NewEngine(FakeApi api, InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return engine;
    }

    private static QuickAddParser Parser() => new(new FixedClock(Today));

    private static InMemorySnapshotStore SeededStore()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Cached task"}""");
        return store;
    }

    private static InMemorySnapshotStore OrderedStore()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","child_order":2}""");
        return store;
    }
}
