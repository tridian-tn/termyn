using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// Which id Todoist itself knows a task by, which is what a link to it outside the app has to carry.
/// </summary>
public class ServerIdTests
{
    private static SyncEngine Engine(FakeApi api, InMemorySnapshotStore? store = null)
    {
        var engine = new SyncEngine(api, store ?? new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    /// <summary>Lets the next sync accept everything it was sent, renaming whatever it was told to.</summary>
    private static void Accepting(FakeApi api, IReadOnlyDictionary<string, string>? renames = null)
        => api.Next = cmds => new SyncResponse
        {
            SyncToken = "s1",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
            TempIdMapping = renames ?? new Dictionary<string, string>(),
        };

    [Fact]
    public void A_task_the_server_sent_goes_by_the_id_it_was_sent_with()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "6XR4GqQQCW6Gv9h4", """{"id":"6XR4GqQQCW6Gv9h4","content":"Buy milk"}""");

        Assert.Equal("6XR4GqQQCW6Gv9h4", Engine(new FakeApi(), store).ServerIdOf("6XR4GqQQCW6Gv9h4"));
    }

    [Fact]
    public void A_task_made_here_has_none_until_the_server_has_taken_it()
    {
        // Added offline, or just not synced yet. The name it goes by is ours, and a link carrying it
        // would be a page Todoist doesn't have.
        var engine = Engine(new FakeApi());

        var temp = engine.AddItem(new JsonObject { ["content"] = "New task" });

        Assert.Null(engine.ServerIdOf(temp));
    }

    [Fact]
    public async Task Once_synced_the_name_we_gave_it_leads_to_the_servers()
    {
        // The outline can still be holding our name when a menu asks, if the sync landed between
        // the render and the click.
        var api = new FakeApi();
        var engine = Engine(api);
        var temp = engine.AddItem(new JsonObject { ["content"] = "New task" });

        Accepting(api, new Dictionary<string, string> { [temp] = "real1" });
        await engine.SyncAsync();

        Assert.Equal("real1", engine.ServerIdOf(temp));
        Assert.Equal("real1", engine.ServerIdOf("real1"));
    }

    [Fact]
    public void A_pending_edit_to_a_task_the_server_has_does_not_count_against_it()
    {
        // Only the create decides it. A task with an edit still queued is one Todoist already has.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "held", """{"id":"held","content":"Buy milk"}""");
        var engine = Engine(new FakeApi(), store);

        engine.UpdateItem("held", new JsonObject { ["content"] = "Buy oat milk" });

        Assert.Equal(1, engine.PendingCount);
        Assert.Equal("held", engine.ServerIdOf("held"));
    }

    [Fact]
    public async Task A_completed_task_fetched_from_the_archive_has_one()
    {
        // Held apart from the model and not editable from here, but it's still on the server, and
        // its page there is the one place left to see the whole of it.
        var api = new FakeApi
        {
            Completed = _ => new CompletedPage(
                [Json.Change("items", "done1", """{"id":"done1","content":"Book dentist","checked":true}""")],
                null),
        };
        var engine = Engine(api);

        await engine.FetchCompletedAsync();

        Assert.False(engine.Holds("done1"));
        Assert.Equal("done1", engine.ServerIdOf("done1"));
    }

    [Fact]
    public void A_task_nothing_here_holds_has_none()
        => Assert.Null(Engine(new FakeApi()).ServerIdOf("gone"));

    [Fact]
    public void A_project_with_the_id_does_not_answer_for_a_task()
    {
        // Ids are only unique within a type, so a project that happens to share one says nothing
        // about whether there's a task by that name.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "shared", """{"id":"shared","name":"Work"}""");

        Assert.Null(Engine(new FakeApi(), store).ServerIdOf("shared"));
    }
}
