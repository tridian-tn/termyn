using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// What happens to an id this engine handed out once the server has renamed the thing behind it.
/// </summary>
/// <remarks>
/// A task created here is named by us until a sync fetches the server's name for it, and that name
/// is what everything is holding in the meantime — the notes box, the outline, an open dialog. The
/// rename used to be the end of them: the model no longer held that id, so an edit arriving against
/// one found no such task and was dropped without a word or a queued command.
///
/// The shape of it is ordinary. Quick-add a task, start writing its description, and the sync lands
/// while you type.
/// </remarks>
public class PromotedIdTests
{
    private static SyncEngine Engine(FakeApi api)
    {
        var engine = new SyncEngine(api, new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" });
        engine.Load();
        return engine;
    }

    /// <summary>Creates a task, then lets a sync land that gives it the server's name.</summary>
    private static async Task<(SyncEngine Engine, string Temp)> Promoted(FakeApi api, string real = "real1")
    {
        var engine = Engine(api);
        var temp = engine.AddItem(new JsonObject { ["content"] = "New task" });

        api.Next = cmds => new SyncResponse
        {
            SyncToken = "s1",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = real },
        };
        await engine.SyncAsync();

        return (engine, temp);
    }

    private static TaskItem? Task(SyncEngine engine, string id)
        => engine.Snapshot().Items.FirstOrDefault(i => i.Id == id);

    [Fact]
    public async Task A_description_written_against_the_name_we_gave_it_reaches_the_task()
    {
        // The one that cost something. Typing a description into a task you have just created, and
        // having the sync land before it saves, used to lose what you wrote — no error, no queued
        // command, and the box cleared itself a moment later.
        var api = new FakeApi();
        var (engine, temp) = await Promoted(api);

        engine.UpdateItem(temp, new JsonObject { ["description"] = "what the user typed" });

        Assert.Equal("what the user typed", Task(engine, "real1")?.Description);
        Assert.Single(engine.Outbox, c => c.Type == "item_update");
    }

    [Fact]
    public async Task The_edit_goes_out_under_the_name_the_server_knows()
    {
        // Queued against the old name it could only ever be refused, and refused commands poison.
        var api = new FakeApi();
        var (engine, temp) = await Promoted(api);

        engine.UpdateItem(temp, new JsonObject { ["description"] = "typed" });

        var command = engine.Outbox.Single(c => c.Type == "item_update");
        Assert.Equal("real1", JsonNode.Parse(command.ArgsJson)!["id"]!.ToString());
    }

    [Fact]
    public async Task Completing_it_by_the_old_name_completes_the_right_task()
    {
        var api = new FakeApi();
        var (engine, temp) = await Promoted(api);

        engine.CompleteItem(temp);

        Assert.Single(engine.Outbox, c => c.Type == "item_close");
        Assert.Equal("real1", JsonNode.Parse(engine.Outbox.Single(c => c.Type == "item_close").ArgsJson)!["id"]!.ToString());
    }

    [Fact]
    public async Task Deleting_it_by_the_old_name_deletes_the_right_task()
    {
        var api = new FakeApi();
        var (engine, temp) = await Promoted(api);

        engine.DeleteItem(temp);

        Assert.Null(Task(engine, "real1"));
    }

    [Fact]
    public async Task Asking_about_it_by_the_old_name_still_finds_it()
    {
        // What the window asks before it will let you type into the notes at all. Answering no left
        // the box read-only on a task that was perfectly editable.
        var api = new FakeApi();
        var (engine, temp) = await Promoted(api);
        engine.UpdateItem("real1", new JsonObject { ["description"] = "already there" });

        Assert.True(engine.Holds(temp));
        Assert.Equal("already there", engine.DescriptionOf(temp));
    }

    [Fact]
    public async Task A_reminder_on_the_old_name_hangs_off_the_right_task()
    {
        // This one carries the id twice — in the lookup and in the arguments — so both have to be
        // put right or the server is sent a reminder for a task it has never heard of.
        var api = new FakeApi();
        var (engine, temp) = await Promoted(api);

        var reminder = engine.AddRelativeReminder(temp, 30);

        Assert.NotNull(reminder);

        var command = engine.Outbox.Single(c => c.Type == "reminder_add");
        Assert.Equal("real1", JsonNode.Parse(command.ArgsJson)!["item_id"]!.ToString());
    }

    [Fact]
    public async Task Labels_set_against_the_old_name_land_on_the_task()
    {
        var api = new FakeApi();
        var (engine, temp) = await Promoted(api);

        engine.SetItemLabels(temp, ["home"]);

        Assert.Equal(["home"], Task(engine, "real1")?.Labels);
    }

    [Fact]
    public async Task A_project_renamed_by_the_name_we_gave_it_is_renamed()
    {
        // Projects, sections and labels are created the same way and were losing the same edits.
        var api = new FakeApi();
        var engine = Engine(api);
        var temp = engine.AddProject("New project");

        api.Next = cmds => new SyncResponse
        {
            SyncToken = "s1",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
            TempIdMapping = new Dictionary<string, string> { [temp] = "p9" },
        };
        await engine.SyncAsync();

        engine.RenameProject(temp, "Renamed");

        Assert.Equal("Renamed", engine.Snapshot().Projects.Single(p => p.Id == "p9").Name);
    }

    // ---- What it still refuses -----------------------------------------------------------------

    [Fact]
    public void An_id_that_was_never_ours_is_left_alone()
    {
        // The old name only answers for something we named. Everything else goes through untouched,
        // including an id for a task that has genuinely gone.
        var engine = Engine(new FakeApi());

        engine.UpdateItem("never-existed", new JsonObject { ["description"] = "nothing to write on" });

        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public async Task A_task_deleted_after_being_promoted_is_still_gone()
    {
        // The old name resolving must not bring a deleted task back from the dead: it answers with
        // the new name, and there is nothing under it.
        var api = new FakeApi();
        var (engine, temp) = await Promoted(api);
        engine.DeleteItem("real1");

        engine.UpdateItem(temp, new JsonObject { ["description"] = "typed at a ghost" });

        Assert.False(engine.Holds(temp));
        Assert.DoesNotContain(engine.Outbox, c => c.Type == "item_update");
    }
}
