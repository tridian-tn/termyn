using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Several tasks selected at once: what the menus offer over them, what the panel says, and what
/// each of the four things that can be done to several does.
/// </summary>
public class SeveralTasksTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    // ---- What the menus offer ------------------------------------------------------------------

    public static TheoryData<AppCommand> TaskCommands()
        => new(Enum.GetValues<AppCommand>().Where(Commands.IsTaskCommand));

    [Theory]
    [MemberData(nameof(TaskCommands))]
    public void Over_several_only_what_can_act_on_several_is_offered(AppCommand command)
    {
        // Everything else is about one task: its name, its dates, its place among its neighbours.
        var context = new CommandContext(
            Abilities: new TaskAbilities(true, true, true, true, true, true, true),
            Tasks: [Row("a", due: "Today"), Row("b", due: "Today")],
            Schedule: ScheduleDays.From(Today, DayOfWeek.Saturday, DayOfWeek.Monday));

        Assert.Equal(Commands.ActsOnSeveral(command), Commands.StateOf(command, context).Enabled);
    }

    [Fact]
    public void Ticking_off_priorities_deleting_and_moving_are_what_act_on_several()
        => Assert.Equal(
            [AppCommand.ToggleComplete, AppCommand.Priority1, AppCommand.Priority2, AppCommand.Priority3, AppCommand.Priority4, AppCommand.MoveTo, AppCommand.Delete],
            Enum.GetValues<AppCommand>().Where(Commands.ActsOnSeveral));

    [Fact]
    public void Over_a_mix_of_done_and_open_it_offers_to_tick_them_off()
        => Assert.Equal(
            Strings.CommandComplete,
            Commands.StateOf(AppCommand.ToggleComplete, Several(Row("a", completed: true), Row("b"))).Label);

    [Fact]
    public void Over_several_all_done_it_offers_to_put_them_back()
        => Assert.Equal(
            Strings.CommandReopen,
            Commands.StateOf(AppCommand.ToggleComplete, Several(Row("a", completed: true), Row("b", completed: true))).Label);

    [Fact]
    public void A_priority_is_ticked_only_when_every_one_of_them_has_it()
    {
        Assert.True(Commands.StateOf(AppCommand.Priority1, Several(Row("a", Priority.P1), Row("b", Priority.P1))).Checked);
        Assert.False(Commands.StateOf(AppCommand.Priority1, Several(Row("a", Priority.P1), Row("b", Priority.P2))).Checked);
    }

    [Fact]
    public void Moving_several_is_greyed_when_one_of_them_cant_go()
        => Assert.False(Commands.StateOf(AppCommand.MoveTo, Several(Row("a"), Row("b")) with { Abilities = new TaskAbilities() }).Enabled);

    [Fact]
    public void Nothing_selected_still_offers_nothing()
        => Assert.All(
            Enum.GetValues<AppCommand>().Where(Commands.IsTaskCommand),
            c => Assert.False(Commands.StateOf(c, CommandContext.Empty).Enabled, c.ToString()));

    // ---- The panel -----------------------------------------------------------------------------

    [Fact]
    public void Several_selected_is_about_none_of_them()
    {
        // Not the project either, whose description would be open to edit under a heading that
        // named it while the user's attention was on three of its tasks.
        var project = new SidebarNode(SidebarKind.Project, "p", "Work", 1, "project:p");

        var subject = PanelSubject.Of(Row("a"), project, selected: 3);

        Assert.Equal(SubjectKind.None, subject.Kind);
        Assert.Null(subject.Id);
        Assert.Equal("3 tasks selected", subject.About);
    }

    [Fact]
    public void One_selected_is_still_about_that_one()
        => Assert.Equal(SubjectKind.Task, PanelSubject.Of(Row("a"), null, selected: 1).Kind);

    // ---- Ticking off ---------------------------------------------------------------------------

    [Fact]
    public void Ticking_off_a_parent_and_its_sub_task_together_closes_the_parent_alone()
    {
        // The parent takes the sub-task with it. A close of its own would be a second one for a
        // task the first has already finished.
        var presenter = NewPresenter(Family(), out var engine);

        presenter.Complete(["parent", "child"]);

        Assert.Equal(["parent"], Asked(engine, "item_close"));
        Assert.True(Item(engine, "child").Completed);
    }

    [Fact]
    public void A_sub_task_picked_out_with_a_repeating_parent_is_ticked_off_in_its_own_right()
    {
        // A repeating task moves on to its next date rather than being finished, and takes nothing
        // under it along, so leaving the sub-task to it left the sub-task open.
        var store = Family();
        store.PutResource("items", "repeat", """{"id":"repeat","content":"Water the plants","project_id":"work","child_order":3,"due":{"date":"2026-07-31","string":"every day","is_recurring":true}}""");
        Put(store, "under", "Fill the can", parent: "repeat");
        var presenter = NewPresenter(store, out var engine);

        presenter.Complete(["repeat", "under"]);

        Assert.Equal(["repeat", "under"], Asked(engine, "item_close").Order());
        Assert.True(Item(engine, "under").Completed);
    }

    [Fact]
    public void Tasks_ticked_off_together_come_back_with_one_undo()
    {
        var presenter = NewPresenter(Family(), out var engine);
        presenter.Complete(["parent", "other"]);

        Assert.True(presenter.Undo());

        Assert.All(["parent", "child", "other"], id => Assert.False(Item(engine, id).Completed));
    }

    [Fact]
    public void Each_task_ticked_off_together_has_its_own_line_in_the_history()
    {
        var presenter = NewPresenter(Family(), out _);

        presenter.Complete(["parent", "other"]);

        Assert.Equal(["Completed “Other”", "Completed “Parent”"], presenter.History.Recent().Select(h => h.Said).Take(2).Order());
    }

    [Fact]
    public void Tasks_put_back_together_are_open_again()
    {
        var presenter = NewPresenter(Family(), out var engine);
        presenter.Complete(["parent"]);
        presenter.Complete(["other"]);

        presenter.Reopen(["parent", "other"]);

        Assert.All(["parent", "child", "other"], id => Assert.False(Item(engine, id).Completed));
    }

    // ---- Deleting ------------------------------------------------------------------------------

    [Fact]
    public void Deleting_a_parent_and_its_sub_task_together_deletes_the_parent_alone()
    {
        var presenter = NewPresenter(Family(), out var engine);

        presenter.Delete(["child", "parent"]);

        Assert.Equal(["parent"], Asked(engine, "item_delete"));
    }

    [Fact]
    public async Task Deleting_a_completed_parent_and_sub_task_out_of_the_archive_deletes_the_parent_alone()
    {
        // The archive's tasks aren't in the model, so their parents were invisible, and the sub-task
        // got a delete of its own. The server deletes it with its parent, and the second delete
        // names a task that's gone.
        var api = new FakeApi
        {
            Completed = _ => new CompletedPage(
            [
                Json.Change("items", "ap", """{"id":"ap","content":"Done parent","project_id":"work","checked":true}"""),
                Json.Change("items", "ac", """{"id":"ac","content":"Done child","project_id":"work","parent_id":"ap","checked":true}"""),
            ], null),
        };
        var presenter = NewPresenter(Family(), out var engine, api);
        await engine.FetchCompletedAsync(TestContext.Current.CancellationToken);

        presenter.Delete(["ap", "ac"]);

        Assert.Equal(["ap"], Asked(engine, "item_delete"));
    }

    [Fact]
    public async Task A_sub_task_named_as_it_was_before_a_sync_still_goes_with_its_parent()
    {
        // The outline can still hold a task under the name it was made with, between a sync giving
        // it the server's and the rows being drawn again.
        var api = new FakeApi
        {
            Next = commands => new SyncResponse
            {
                SyncToken = "s1",
                SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
                TempIdMapping = commands.Where(c => c.TempId is not null).ToDictionary(c => c.TempId!, c => "real-" + c.TempId),
            },
        };
        var presenter = NewPresenter(Family(), out var engine, api);
        var parent = engine.AddItem(new JsonObject { ["content"] = "New parent", ["project_id"] = "work" });
        var child = engine.AddItem(new JsonObject { ["content"] = "New child", ["project_id"] = "work", ["parent_id"] = parent });
        await engine.SyncAsync(TestContext.Current.CancellationToken);

        presenter.Delete([parent, child]);

        Assert.Equal(["real-" + parent], Asked(engine, "item_delete"));
    }

    [Fact]
    public void Tasks_deleted_together_come_back_with_one_undo()
    {
        var presenter = NewPresenter(Family(), out var engine);
        presenter.Delete(["parent", "other"]);

        Assert.True(presenter.Undo());

        Assert.Equal(["child", "other", "parent"], engine.Snapshot().Items.Select(i => i.Id).Order());
    }

    // ---- A priority ----------------------------------------------------------------------------

    [Fact]
    public void A_priority_given_to_several_goes_on_each_and_passes_over_those_that_have_it()
    {
        var store = Family();
        Put(store, "other", "Other", priority: 4);   // API 4 is P1
        var presenter = NewPresenter(store, out var engine);

        Assert.True(presenter.SetPriority(["parent", "other"], Priority.P1));

        Assert.Equal(["parent"], Asked(engine, "item_update"));
        Assert.Equal(Priority.P1, Item(engine, "parent").Priority);
    }

    [Fact]
    public void A_priority_they_all_have_already_writes_nothing()
    {
        var presenter = NewPresenter(Family(), out var engine);

        Assert.False(presenter.SetPriority(["parent", "other"], Priority.P4));

        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public void A_priority_is_noted_only_for_the_tasks_it_could_be_given_to()
    {
        // A task the model doesn't hold, such as a completed one out of the archive, is declined
        // the edit. Counted anyway, the history said it had been changed.
        var presenter = NewPresenter(Family(), out var engine);

        Assert.True(presenter.SetPriority(["parent", "gone"], Priority.P1));

        Assert.Equal(["parent"], Asked(engine, "item_update"));
        Assert.Single(presenter.History.Recent());
    }

    [Fact]
    public void A_priority_for_tasks_none_of_which_is_held_writes_nothing_and_says_so()
    {
        var presenter = NewPresenter(Family(), out var engine);

        Assert.False(presenter.SetPriority(["gone", "also-gone"], Priority.P1));

        Assert.Empty(engine.Outbox);
        Assert.Empty(presenter.History.Recent());
    }

    [Fact]
    public void A_priority_given_to_several_is_saved_in_one_commit()
    {
        // Each commit waits on the disk, so one for each task made a long run of them slow.
        var store = new FillingStore(Family());
        var presenter = NewPresenter(store, out _);

        presenter.SetPriority(["parent", "child", "other"], Priority.P1);

        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public void A_priority_the_store_gives_out_on_partway_changes_nothing_and_notes_nothing()
    {
        // Noted as each was written, the history said the first had been changed when the store
        // had taken it back with the rest.
        var store = new FillingStore(Family()) { WritesLeft = 1 };
        var presenter = NewPresenter(store, out var engine);

        Assert.Throws<IOException>(() => presenter.SetPriority(["parent", "other"], Priority.P1));

        Assert.Equal(Priority.P4, Item(engine, "parent").Priority);
        Assert.Empty(presenter.History.Recent());
    }

    // ---- Moving --------------------------------------------------------------------------------

    [Fact]
    public void Several_moved_together_are_saved_in_one_commit()
    {
        var store = new FillingStore(Family());
        var presenter = NewPresenter(store, out _);
        var home = presenter.DestinationsFor(["parent", "other"]).Single(d => d.Path == "Home");

        Assert.Equal(2, presenter.MoveTo(["parent", "other"], home));

        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public void A_move_the_store_gives_out_on_partway_changes_nothing_and_notes_nothing()
    {
        var store = new FillingStore(Family()) { WritesLeft = 1 };
        var presenter = NewPresenter(store, out var engine);
        var home = presenter.DestinationsFor(["parent", "other"]).Single(d => d.Path == "Home");

        Assert.Throws<IOException>(() => presenter.MoveTo(["parent", "other"], home));

        Assert.Equal("work", Item(engine, "parent").ProjectId);
        Assert.Empty(presenter.History.Recent());
    }

    [Fact]
    public void Moving_several_sends_each_and_leaves_a_sub_task_under_its_parent()
    {
        var presenter = NewPresenter(Family(), out var engine);
        var home = presenter.DestinationsFor(["parent", "child", "other"]).Single(d => d.Path == "Home");

        Assert.Equal(2, presenter.MoveTo(["parent", "child", "other"], home));

        Assert.Equal("home", Item(engine, "parent").ProjectId);
        Assert.Equal("home", Item(engine, "other").ProjectId);
        Assert.Equal("parent", Item(engine, "child").ParentId);
    }

    [Fact]
    public void The_place_they_all_sit_is_marked_as_where_they_are()
    {
        var presenter = NewPresenter(Family(), out _);

        Assert.Equal("work", presenter.DestinationsFor(["parent", "other"]).Single(d => d.Here).Id);
    }

    [Fact]
    public void A_sub_task_picked_out_with_its_parent_doesnt_stop_the_parents_place_being_marked()
    {
        // The sub-task goes with its parent, so only the parent's place counts. Counting the
        // sub-task's left the parent's own project on offer, and choosing it moved nothing.
        var presenter = NewPresenter(Family(), out _);

        Assert.Equal("work", presenter.DestinationsFor(["parent", "child"]).Single(d => d.Here).Id);
    }

    [Fact]
    public void Nowhere_is_marked_when_they_sit_in_different_places()
    {
        // The picker won't send a task where it's marked as sitting, and the one not there yet has
        // every reason to go.
        var store = Family();
        Put(store, "other", "Other", project: "home");
        var presenter = NewPresenter(store, out _);

        Assert.DoesNotContain(presenter.DestinationsFor(["parent", "other"]), d => d.Here);
    }

    [Fact]
    public void Several_can_be_moved_only_when_every_one_of_them_is_held()
    {
        var presenter = NewPresenter(Family(), out _);

        Assert.True(presenter.AbilitiesForAll(["parent", "other"]).CanMoveTo);
        Assert.False(presenter.AbilitiesForAll(["parent", "gone"]).CanMoveTo);
    }

    [Fact]
    public void What_several_can_do_together_is_only_a_move()
    {
        // Indenting, nudging and the rest turn on one task's place among its neighbours.
        var presenter = NewPresenter(Family(), out _);

        Assert.Equal(new TaskAbilities(CanMoveTo: true), presenter.AbilitiesForAll(["parent", "other"]));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static TaskRow Row(string id, Priority priority = Priority.P4, bool completed = false, string due = "")
        => new(id, id, priority, "Work", due, [], Completed: completed);

    private static CommandContext Several(params TaskRow[] rows)
        => new(Abilities: new TaskAbilities(CanMoveTo: true), Tasks: rows);

    /// <summary>The tasks the outbox names in commands of one kind, in the order it'll send them.</summary>
    private static List<string> Asked(SyncEngine engine, string type)
        => engine.Outbox
            .Where(c => c.Type == type)
            .Select(c => JsonNode.Parse(c.ArgsJson)!["id"]!.ToString())
            .ToList();

    private static TaskItem Item(SyncEngine engine, string id) => engine.Snapshot().Items.Single(i => i.Id == id);

    /// <summary>
    /// Two projects, and in Work a parent with a sub-task under it and another task beside them.
    /// </summary>
    private static InMemorySnapshotStore Family()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "work", """{"id":"work","name":"Work","child_order":1}""");
        store.PutResource("projects", "home", """{"id":"home","name":"Home","child_order":2}""");
        Put(store, "parent", "Parent", order: 1);
        Put(store, "child", "Child", parent: "parent");
        Put(store, "other", "Other", order: 2);
        return store;
    }

    private static void Put(
        InMemorySnapshotStore store,
        string id,
        string content,
        string project = "work",
        string? parent = null,
        int order = 1,
        int priority = 1)
    {
        var item = new JsonObject
        {
            ["id"] = id,
            ["content"] = content,
            ["project_id"] = project,
            ["child_order"] = order,
            ["priority"] = priority,
        };

        if (parent is not null) item["parent_id"] = parent;

        store.PutResource("items", id, item.ToJsonString());
    }

    private static MainPresenter NewPresenter(ISnapshotStore store, out SyncEngine engine, FakeApi? api = null)
    {
        engine = new SyncEngine(api ?? new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
    }
}
