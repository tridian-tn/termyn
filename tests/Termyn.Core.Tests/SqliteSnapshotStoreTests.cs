using System.Text.Json.Nodes;
using Termyn.Core.Sync;

namespace Termyn.Core.Tests;

public class SqliteSnapshotStoreTests
{
    [Fact]
    public void Resources_outbox_and_token_survive_a_reopen_with_unknown_fields_intact()
    {
        var path = TempDbPath();
        try
        {
            using (var store = new SqliteSnapshotStore(path))
            {
                store.PutResource("items", "i1", """{"id":"i1","content":"A","weird_field":"keep"}""");
                store.SaveSync([], [], "token-1");
                store.ApplyLocalWrite(Cmd("u1", "item_update", args: """{"id":"i1","content":"B"}"""), null, null);
            }

            using (var store = new SqliteSnapshotStore(path))
            {
                var snapshot = store.Load();

                Assert.Equal("token-1", snapshot.SyncToken);

                var obj = (JsonObject)JsonNode.Parse(Assert.Single(snapshot.Resources).Json)!;
                Assert.Equal("keep", obj["weird_field"]!.ToString());

                Assert.Equal("u1", Assert.Single(snapshot.Outbox).Uuid);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void An_optimistic_write_commits_the_resource_and_its_command_together()
    {
        var path = TempDbPath();
        try
        {
            using (var store = new SqliteSnapshotStore(path))
            {
                store.ApplyLocalWrite(
                    Cmd("u1", "item_add", tempId: "t-1", args: """{"content":"New"}"""),
                    new StoredResource("items", "t-1", """{"id":"t-1","content":"New"}"""),
                    null);
            }

            using (var store = new SqliteSnapshotStore(path))
            {
                var snapshot = store.Load();
                Assert.Equal("t-1", Assert.Single(snapshot.Resources).Id);
                Assert.Equal("t-1", Assert.Single(snapshot.Outbox).TempId);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void A_local_delete_removes_the_resource_as_the_command_is_queued()
    {
        var path = TempDbPath();
        try
        {
            using var store = new SqliteSnapshotStore(path);
            store.PutResource("items", "i1", """{"id":"i1"}""");

            store.ApplyLocalWrite(Cmd("u1", "item_delete", args: """{"id":"i1"}"""), null, new ResourceKey("items", "i1"));

            Assert.Empty(store.Load().Resources);
            Assert.Single(store.Load().Outbox);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Command_attempts_state_and_error_survive_a_reopen()
    {
        var path = TempDbPath();
        try
        {
            using (var store = new SqliteSnapshotStore(path))
            {
                var cmd = Cmd("u1", "item_update", args: """{"id":"i1"}""", prior: """{"id":"i1","content":"A"}""");
                store.ApplyLocalWrite(cmd, null, null);

                cmd.Attempts = 3;
                cmd.NoVerdictRounds = 2;
                cmd.State = OutboxState.Failed;
                cmd.LastError = "boom";
                store.UpdateCommand(cmd);
            }

            using (var store = new SqliteSnapshotStore(path))
            {
                var cmd = Assert.Single(store.Load().Outbox);
                Assert.Equal(3, cmd.Attempts);
                Assert.Equal(2, cmd.NoVerdictRounds);
                Assert.Equal(OutboxState.Failed, cmd.State);
                Assert.Equal("boom", cmd.LastError);
                Assert.Equal("""{"id":"i1","content":"A"}""", cmd.PriorJson);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SaveSync_applies_upserts_deletes_and_the_token_together()
    {
        var path = TempDbPath();
        try
        {
            using var store = new SqliteSnapshotStore(path);
            store.PutResource("items", "stays", """{"id":"stays","content":"old"}""");
            store.PutResource("items", "goes", """{"id":"goes"}""");

            store.SaveSync(
                [new StoredResource("items", "stays", """{"id":"stays","content":"new"}"""), new StoredResource("items", "fresh", """{"id":"fresh"}""")],
                [new ResourceKey("items", "goes")],
                "token-2");

            var snapshot = store.Load();
            Assert.Equal(new[] { "fresh", "stays" }, snapshot.Resources.Select(r => r.Id).OrderBy(id => id).ToArray());
            Assert.Contains("new", snapshot.Resources.Single(r => r.Id == "stays").Json);
            Assert.Equal("token-2", snapshot.SyncToken);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Rename_and_delete_resources_round_trip()
    {
        var path = TempDbPath();
        try
        {
            using var store = new SqliteSnapshotStore(path);
            store.PutResource("items", "temp", """{"id":"temp"}""");
            store.RenameResource("items", "temp", "real");
            store.PutResource("items", "gone", """{"id":"gone"}""");
            store.DeleteResource("items", "gone");

            Assert.Equal(new[] { "real" }, store.Load().Resources.Select(r => r.Id).ToArray());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Renaming_onto_an_existing_id_replaces_it_instead_of_failing()
    {
        var path = TempDbPath();
        try
        {
            using var store = new SqliteSnapshotStore(path);
            store.PutResource("items", "temp", """{"id":"temp","content":"incoming"}""");
            store.PutResource("items", "real", """{"id":"real","content":"existing"}""");

            store.RenameResource("items", "temp", "real");

            var resource = Assert.Single(store.Load().Resources);
            Assert.Equal("real", resource.Id);
            Assert.Contains("incoming", resource.Json);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Deleted_commands_do_not_return_after_reopen()
    {
        var path = TempDbPath();
        try
        {
            using (var store = new SqliteSnapshotStore(path))
            {
                store.ApplyLocalWrite(Cmd("keep", "item_close"), null, null);
                store.ApplyLocalWrite(Cmd("drop", "item_delete"), null, null);
                store.DeleteCommands(["drop"]);
            }

            using (var store = new SqliteSnapshotStore(path))
                Assert.Equal("keep", Assert.Single(store.Load().Outbox).Uuid);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Purge_erases_everything_and_leaves_no_write_ahead_log_behind()
    {
        var path = TempDbPath();
        try
        {
            using (var store = new SqliteSnapshotStore(path))
            {
                store.PutResource("items", "i1", """{"id":"i1","content":"Private"}""");
                store.ApplyLocalWrite(Cmd("u1", "item_update", args: """{"id":"i1"}"""), null, null);
                store.SaveSync([], [], "token-1");

                store.Purge();

                var snapshot = store.Load();
                Assert.Empty(snapshot.Resources);
                Assert.Empty(snapshot.Outbox);
                Assert.Equal("*", snapshot.SyncToken);
            }

            var wal = new FileInfo(path + "-wal");
            Assert.True(!wal.Exists || wal.Length == 0);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static OutboxCommand Cmd(string uuid, string type, string? tempId = null, string args = "{}", string? prior = null)
        => new() { Uuid = uuid, Type = type, TempId = tempId, ArgsJson = args, PriorJson = prior };

    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), "termyn-test-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Cleanup(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(file))
                File.Delete(file);
    }
}
