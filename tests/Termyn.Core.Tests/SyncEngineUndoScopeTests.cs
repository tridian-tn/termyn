using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// How much of a task an undo puts back.
/// </summary>
/// <remarks>
/// Undoing a close means "I didn't mean to tick that off". It does not mean "put this task back to
/// whatever it was at that moment" — anything edited since is a later intention, and taking it away
/// is a silent loss of work the user can't see happening.
/// </remarks>
public class SyncEngineUndoScopeTests
{
    // ---- Before the close has been sent --------------------------------------------------------

    [Fact]
    public void An_edit_made_after_an_unsent_close_survives_undoing_it()
    {
        var engine = Seeded();
        engine.CompleteItem("i1");

        engine.UpdateItem("i1", new JsonObject { ["description"] = "written after" });

        Assert.True(engine.Undo());

        var item = Item(engine, "i1");
        Assert.Equal("written after", item.Description);
        Assert.False(item.Completed);
    }

    [Theory]
    [InlineData("content", "renamed after")]
    [InlineData("description", "noted after")]
    public void It_is_every_field_and_not_the_description_in_particular(string field, string value)
    {
        var engine = Seeded();
        engine.CompleteItem("i1");
        engine.UpdateItem("i1", new JsonObject { [field] = value });

        Assert.True(engine.Undo());

        var json = engine.Snapshot().Items.Single(i => i.Id == "i1");
        Assert.Equal(value, field == "content" ? json.Content : json.Description);
    }

    [Fact]
    public void The_priority_set_after_an_unsent_close_survives_it_too()
    {
        var engine = Seeded();
        engine.CompleteItem("i1");
        engine.UpdateItem("i1", new JsonObject { ["priority"] = 4 });   // API 4 is P1

        Assert.True(engine.Undo());

        Assert.Equal(Priority.P1, Item(engine, "i1").Priority);
    }

    [Fact]
    public void A_due_date_set_after_an_unsent_close_survives_it_too()
    {
        // Worth its own case rather than another row in the theory above: a due date is a nested
        // object where the others are one value, and it is the field the whole outline is arranged
        // by — losing one silently moves a task to a different day.
        var engine = Seeded();
        engine.CompleteItem("i1");
        engine.UpdateItem("i1", new JsonObject { ["due"] = ItemFields.Due(new DateOnly(2026, 8, 20), null) });

        Assert.True(engine.Undo());

        var item = Item(engine, "i1");
        Assert.Equal("2026-08-20", item.DueDate);
        Assert.False(item.Completed);
    }

    [Fact]
    public void A_due_date_cleared_after_an_unsent_close_stays_cleared()
    {
        // The other direction, which a restore would put back rather than take away.
        var engine = Seeded(due: """{"date":"2026-08-01"}""");
        engine.CompleteItem("i1");
        engine.UpdateItem("i1", new JsonObject { ["due"] = null });

        Assert.True(engine.Undo());

        Assert.Null(Item(engine, "i1").DueDate);
    }

    [Fact]
    public void The_close_that_never_went_is_taken_out_of_the_outbox()
    {
        // Undone before it was sent, so there is nothing for the server to hear about. Left queued
        // it would arrive later and tick the task off all over again.
        var engine = Seeded();
        engine.CompleteItem("i1");

        Assert.True(engine.Undo());

        Assert.DoesNotContain(engine.Outbox, c => c.Type == "item_close");
    }

    [Fact]
    public void Undoing_a_close_on_its_own_still_reopens_the_task()
    {
        // The ordinary case, and the one everything else here must not break.
        var engine = Seeded();
        engine.CompleteItem("i1");

        Assert.True(engine.Undo());

        Assert.False(Item(engine, "i1").Completed);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Undoing_a_close_does_not_queue_an_uncomplete_for_a_close_that_never_left()
    {
        // Telling the server to reopen a task it was never told about would be an edit out of
        // nowhere — and one that fails, because there is nothing there to reopen.
        var engine = Seeded();
        engine.CompleteItem("i1");

        Assert.True(engine.Undo());

        Assert.DoesNotContain(engine.Outbox, c => c.Type == "item_uncomplete");
    }

    [Fact]
    public void The_edit_made_after_the_close_is_still_going_to_be_sent()
    {
        // Kept on screen and kept queued: the two have to agree, or the next sync puts back what
        // the undo appeared to remove.
        var engine = Seeded();
        engine.CompleteItem("i1");
        engine.UpdateItem("i1", new JsonObject { ["description"] = "written after" });

        engine.Undo();

        Assert.Contains(engine.Outbox, c => c.Type == "item_update");
    }

    // ---- Once the close has been sent ----------------------------------------------------------

    [Fact]
    public async Task An_edit_made_after_a_sent_close_survives_undoing_it()
    {
        // The other path through undo, which reopens the task rather than dropping a command —
        // already the narrow kind of reversal, and this holds it to that.
        var (engine, api) = SeededWithApi();
        engine.CompleteItem("i1");

        await Flush(engine, api);

        engine.UpdateItem("i1", new JsonObject { ["description"] = "written after" });

        Assert.True(engine.Undo());

        var item = Item(engine, "i1");
        Assert.Equal("written after", item.Description);
        Assert.False(item.Completed);
    }

    [Fact]
    public async Task Undoing_a_sent_close_does_tell_the_server_to_reopen_it()
    {
        // The mirror of the unsent case: the server was told, so it has to be told otherwise.
        var (engine, api) = SeededWithApi();
        engine.CompleteItem("i1");

        await Flush(engine, api);

        Assert.True(engine.Undo());

        Assert.Contains(engine.Outbox, c => c.Type == "item_uncomplete");
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>Sends what is queued and has the server accept all of it.</summary>
    private static async Task Flush(SyncEngine engine, FakeApi api)
    {
        api.Next = cmds => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = cmds.ToDictionary(c => c.Uuid, _ => new CommandResult(true, null, null)),
        };

        await engine.SyncAsync();
        Assert.Equal(0, engine.PendingCount);
    }

    private static TaskItem Item(SyncEngine engine, string id)
        => engine.Snapshot().Items.Single(i => i.Id == id);

    private static SyncEngine Seeded(string? due = null) => SeededWithApi(due).Engine;

    private static (SyncEngine Engine, FakeApi Api) SeededWithApi(string? due = null)
    {
        var store = new InMemorySnapshotStore();

        var item = new JsonObject
        {
            ["id"] = "i1",
            ["content"] = "Write it up",
            ["project_id"] = "p",
            ["priority"] = 1,
            ["description"] = "before",
        };

        if (due is not null)
            item["due"] = JsonNode.Parse(due);

        store.PutResource("items", "i1", item.ToJsonString());

        var api = new FakeApi { Response = new SyncResponse { SyncToken = "s1" } };
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(new DateOnly(2026, 7, 31)));
        engine.Load();
        return (engine, api);
    }
}
