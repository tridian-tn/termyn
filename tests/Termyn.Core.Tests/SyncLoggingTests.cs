using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// What the engine writes down, and what it must never write down.
/// </summary>
/// <remarks>
/// The log exists so a bug report has something in it. It is also a file nothing sweeps and nothing
/// encrypts, so the rule that keeps it worth having is that the account's own words stay out of it:
/// ids, counts and the server's verdicts, never a task's content and never the token.
/// </remarks>
public class SyncLoggingTests
{
    private static SyncResponse Resp(string? syncToken, IReadOnlyList<ResourceChange>? changes = null, IReadOnlyDictionary<string, CommandResult>? status = null)
        => new()
        {
            SyncToken = syncToken,
            Changes = changes ?? [],
            SyncStatus = status ?? new Dictionary<string, CommandResult>(),
        };

    [Fact]
    public void A_cached_row_that_cannot_be_read_is_counted_and_never_quoted()
    {
        var log = new RecordingLog();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Buy a birthday present"}""");
        store.PutResource("items", "i2", "{ this is not json");
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, log: log);

        engine.Load();

        Assert.Contains("1 of 2 cached rows couldn't be read", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("this is not json", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("birthday", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public void A_queued_change_that_cannot_be_read_is_counted_too()
    {
        // It stays in the store and is never loaded, so it can never be sent and nothing else would
        // ever mention it. This line is the only sign the user's change went nowhere.
        var log = new RecordingLog();
        var store = new InMemorySnapshotStore();
        store.ApplyLocalWrite(
            new OutboxCommand
            {
                Uuid = "u1",
                Type = "item_update",
                ArgsJson = "{ this is not json",
                State = OutboxState.Pending,
            },
            [],
            []);
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, log: log);

        engine.Load();

        Assert.Equal(0, engine.PendingCount);
        Assert.Contains("1 queued changes couldn't be read and will never be sent", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("this is not json", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_command_is_written_down_by_type_and_uuid()
    {
        var log = new RecordingLog();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Buy a birthday present"}""");
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, log: log);
        engine.Load();
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Buy a cake instead" });

        var uuid = engine.Outbox.Single().Uuid;
        api.Next = _ => Resp("s1", status: new Dictionary<string, CommandResult>
        {
            [uuid] = new(false, "INVALID_ARGUMENT", "Invalid argument value"),
        });

        await engine.SyncAsync();

        Assert.Contains($"Todoist refused item_update (uuid {uuid}", log.All, StringComparison.Ordinal);
        Assert.Contains("Invalid argument value", log.All, StringComparison.Ordinal);

        // What the user typed is in that command's arguments, and it stays there.
        Assert.DoesNotContain("cake", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("birthday", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_given_up_on_says_so_once_it_has_been_tried_enough()
    {
        var log = new RecordingLog();
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Buy a birthday present"}""");
        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, attemptCeiling: 2, log: log);
        engine.Load();
        engine.UpdateItem("i1", new JsonObject { ["content"] = "Buy a cake instead" });

        var uuid = engine.Outbox.Single().Uuid;
        api.Next = _ => Resp("s1", status: new Dictionary<string, CommandResult>
        {
            [uuid] = new(false, "INVALID_ARGUMENT", "Invalid argument value"),
        });

        await engine.SyncAsync();
        await engine.SyncAsync();

        Assert.Contains($"Gave up on item_update (uuid {uuid}) after 2 attempts", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_token_is_written_down_but_the_token_itself_never_is()
    {
        // The one thing that must never reach the log, in the one place that handles it.
        const string token = "0123456789abcdef-a-real-looking-token";
        var log = new RecordingLog();
        var secrets = new FakeSecrets { Stored = token };
        var engine = new SyncEngine(new FakeApi { Throw = new TodoistAuthException("no") }, new InMemorySnapshotStore(), secrets, log: log);

        await Assert.ThrowsAsync<TodoistAuthException>(() => engine.SyncAsync());

        Assert.Contains("Todoist rejected the token", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain(token, log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sync_that_goes_through_is_not_worth_a_line()
    {
        // The log is for what went wrong. A line per sync would be a line every three quarters of a
        // minute, all day, and the one that mattered would be lost in them.
        var log = new RecordingLog();
        var engine = new SyncEngine(new FakeApi(), new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" }, log: log);
        engine.Load();

        await engine.SyncAsync();
        await engine.SyncAsync();

        Assert.Empty(log.Lines);
    }
}
