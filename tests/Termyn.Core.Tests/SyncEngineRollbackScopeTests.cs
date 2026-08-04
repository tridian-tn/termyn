using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// How much of a resource a rolled-back write puts back.
/// </summary>
/// <remarks>
/// A write the server refuses has to leave the local copy agreeing with the server about the field
/// it failed to change. It has no business touching anything else — its prior is the whole resource
/// as it stood when the command was queued, and everything else in there is only a photograph of a
/// moment, not something the failure has anything to say about.
/// </remarks>
public class SyncEngineRollbackScopeTests
{
    [Fact]
    public async Task A_failed_edit_does_not_re_complete_a_task_whose_completion_was_undone()
    {
        // The case this was found by. The close is queued, the edit is queued after it — so the
        // edit's prior carries the tick — and then the close is undone. A failed edit used to put
        // the whole prior back, tick and all, leaving the client saying a task was done that the
        // server had never been told about.
        var (engine, api) = Engine();

        engine.CompleteItem("i1");
        engine.UpdateItem("i1", new JsonObject { ["description"] = "written after" });
        Assert.True(engine.Undo());

        await Reject(engine, api);

        var item = engine.Snapshot().Items.Single();
        Assert.False(item.Completed);

        // The field that failed does go back: that write didn't happen, so the local copy returns
        // to what the server has.
        Assert.Equal("before", item.Description);
    }

    [Fact]
    public async Task A_failed_edit_leaves_alone_a_field_it_never_wrote()
    {
        // The general shape, without the completion: two edits, one fails, and the one that landed
        // stays landed.
        var (engine, api) = Engine();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        await Accept(engine, api);

        engine.UpdateItem("i1", new JsonObject { ["description"] = "noted" });
        await Reject(engine, api);

        var item = engine.Snapshot().Items.Single();
        Assert.Equal("Renamed", item.Content);
        Assert.Equal("before", item.Description);
    }

    [Fact]
    public async Task A_failed_edit_still_puts_its_own_field_back()
    {
        // The whole point of a rollback, and the thing narrowing it must not lose.
        var (engine, api) = Engine();

        engine.UpdateItem("i1", new JsonObject { ["content"] = "Renamed" });
        await Reject(engine, api);

        Assert.Equal("Task", engine.Snapshot().Items.Single().Content);
    }

    [Fact]
    public async Task A_field_the_edit_added_is_taken_off_again_rather_than_nulled()
    {
        // Asserted against the stored JSON rather than the projection, which reads an absent field
        // and an explicit null as the same empty string and so can't tell the two apart. The
        // distinction is what "put it back as it was" means: the server never had this key, and
        // writing a null in its place is a copy that doesn't match the account.
        var (store, engine, api) = Fixture(description: null);

        engine.UpdateItem("i1", new JsonObject { ["description"] = "added by the edit" });
        await Reject(engine, api);

        Assert.Equal(string.Empty, engine.Snapshot().Items.Single().Description);
        Assert.DoesNotContain("description", StoredItem(store));
    }

    /// <summary>The task as it is written down, rather than as it is projected.</summary>
    private static string StoredItem(InMemorySnapshotStore store)
        => store.Load().Resources.Single(r => r is { Type: "items", Id: "i1" }).Json;

    [Fact]
    public async Task A_failed_rename_leaves_a_project_starred_if_it_was_starred_since()
    {
        // Not tasks in particular: projects, sections and labels are updated the same way and had
        // the same whole-resource rollback behind them.
        var (engine, api) = Engine();

        // The star goes on after the rename is queued, so the rename's prior still has it off. That
        // ordering is the whole point: a whole-resource restore would put the prior's "not starred"
        // back along with the name.
        engine.RenameProject("p1", "Renamed");
        engine.SetProjectFavorite("p1", true);

        await RejectOnly(engine, api, args => args["name"] is not null);

        var project = engine.Snapshot().Projects.Single(p => p.Id == "p1");
        Assert.Equal("Work", project.Name);
        Assert.True(project.IsFavorite);
    }

    [Fact]
    public async Task A_failed_delete_still_puts_the_whole_thing_back()
    {
        // Nothing left to rewind into, so the whole prior goes back as it always did.
        var (engine, api) = Engine();

        engine.DeleteItem("i1");
        await Reject(engine, api);

        var item = engine.Snapshot().Items.Single();
        Assert.Equal("Task", item.Content);
        Assert.Equal("before", item.Description);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>Has the server refuse everything, twice, so the commands reach their ceiling.</summary>
    private static async Task Reject(SyncEngine engine, FakeApi api)
    {
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(false, "ERR", "rejected")),
        };

        await engine.SyncAsync();
        await engine.SyncAsync();
    }

    /// <summary>Refuses the commands a predicate picks out, and accepts the rest.</summary>
    private static async Task RejectOnly(SyncEngine engine, FakeApi api, Func<JsonObject, bool> failing)
    {
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(
                c => c.Uuid,
                c => failing(c.Args) ? new CommandResult(false, "ERR", "rejected") : new CommandResult(true, null, null)),
        };

        await engine.SyncAsync();
        await engine.SyncAsync();
    }

    private static async Task Accept(SyncEngine engine, FakeApi api)
    {
        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = commands.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };

        await engine.SyncAsync();
        Assert.Equal(0, engine.PendingCount);
    }

    private static (SyncEngine Engine, FakeApi Api) Engine(string? description = "before")
    {
        var (_, engine, api) = Fixture(description);
        return (engine, api);
    }

    private static (InMemorySnapshotStore Store, SyncEngine Engine, FakeApi Api) Fixture(string? description = "before")
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work"}""");

        var item = new JsonObject
        {
            ["id"] = "i1",
            ["content"] = "Task",
            ["project_id"] = "p1",
        };

        if (description is not null)
            item["description"] = description;

        store.PutResource("items", "i1", item.ToJsonString());

        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(new DateOnly(2026, 7, 31)), attemptCeiling: 2);
        engine.Load();
        return (store, engine, api);
    }
}
