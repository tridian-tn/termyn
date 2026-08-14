using Microsoft.Data.Sqlite;

namespace Termyn.Core.Sync;

/// <summary>
/// SQLite-backed <see cref="ISnapshotStore"/>. Resources are stored as raw JSON blobs keyed by
/// type + id, the sync token lives in <c>meta</c>, and the durable command outbox is an ordered
/// table.
/// </summary>
/// <remarks>
/// The database runs in WAL mode, so it is accompanied on disk by <c>-wal</c> and <c>-shm</c>
/// sidecar files holding recent writes. <see cref="Purge"/> and <see cref="Dispose"/> checkpoint
/// and truncate the log so cached task data does not linger beside a deleted database, and
/// <see cref="Purge"/> compacts the file so purged rows are not left readable in freed pages.
/// The database itself is not encrypted.
/// </remarks>
public sealed class SqliteSnapshotStore : ISnapshotStore
{
    private const string SyncTokenKey = "sync_token";

    private readonly SqliteConnection _conn;

    /// <summary>
    /// The two SQLite codes that mean the file will never be readable, whatever we do.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. Most of what SQLite refuses with is temporary — busy, locked, read-only,
    /// or the "unable to open" you get when something else has the file — and none of those is a
    /// reason to throw a cache away. Doing so would lose the outbox to a problem that was about to
    /// resolve itself, which is a worse failure than the one being recovered from.
    /// </remarks>
    private const int Corrupt = 11;       // SQLITE_CORRUPT — "database disk image is malformed"
    private const int NotADatabase = 26;  // SQLITE_NOTADB  — "file is not a database"

    /// <summary>The suffix a cache that couldn't be opened is kept under.</summary>
    /// <remarks>
    /// Kept rather than deleted, because a file that stopped the app starting is the only evidence
    /// of why it did. One copy, overwritten: a growing pile of them in somebody's cache directory
    /// would be a second problem.
    /// </remarks>
    private const string SetAsideSuffix = ".corrupt";

    /// <summary>The sidecars SQLite keeps beside the database in WAL mode.</summary>
    private static readonly string[] Sidecars = ["", "-wal", "-shm"];

    /// <summary>
    /// True when the cache on disk couldn't be opened and this one was started from nothing.
    /// </summary>
    /// <remarks>
    /// Worth saying out loud rather than recovering quietly: the outbox lives in the same file, so
    /// anything queued and not yet sent went with it. Everything else comes back on the first sync.
    /// </remarks>
    public bool Rebuilt { get; }

    public SqliteSnapshotStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        try
        {
            _conn = Open(databasePath);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is Corrupt or NotADatabase)
        {
            // A cache is a cache: everything in it can be fetched again, and the one thing that
            // can't — the outbox — is already lost with the file. Refusing to start is the worst of
            // the answers available, and it is the one that leaves somebody hunting for a file they
            // have to be told is safe to delete.
            SetAside(databasePath);

            _conn = Open(databasePath);
            Rebuilt = true;
        }
    }

    /// <summary>Opens the database and puts the schema in it, disposing the connection if it can't.</summary>
    private static SqliteConnection Open(string databasePath)
    {
        // Pooling off: the store holds one long-lived connection, so pooling adds nothing and keeping
        // it off releases the file handle promptly on Dispose.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        var conn = new SqliteConnection(connectionString);
        try
        {
            conn.Open();
            Execute(conn, "PRAGMA journal_mode=WAL;");
            // Overwrite deleted content rather than leaving it readable in freed pages, so purging a
            // previous account's tasks actually removes them.
            Execute(conn, "PRAGMA secure_delete=ON;");
            Execute(conn, """
                CREATE TABLE IF NOT EXISTS resources (type TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY (type, id));
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS outbox (seq INTEGER PRIMARY KEY AUTOINCREMENT, uuid TEXT NOT NULL, type TEXT NOT NULL, temp_id TEXT, args TEXT NOT NULL, prior TEXT, attempts INTEGER NOT NULL, no_verdict INTEGER NOT NULL DEFAULT 0, state INTEGER NOT NULL, last_error TEXT);
                CREATE TABLE IF NOT EXISTS deferred_deletes (type TEXT NOT NULL, id TEXT NOT NULL, PRIMARY KEY (type, id));
                """);

            return conn;
        }
        catch
        {
            // Or the handle stays on the file, and nothing can move it out of the way.
            conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Moves a cache that couldn't be opened out of the way, sidecars and all.
    /// </summary>
    /// <remarks>
    /// A file that can't be moved is swallowed rather than thrown, because this runs on the way to
    /// starting the app and the whole point of it is that a bad cache doesn't stop that happening.
    /// If the move fails then the open that follows fails too, and that is what gets reported —
    /// which is why only the two failures a locked or unwritable file produces are caught here, and
    /// anything else still comes out.
    /// </remarks>
    private static void SetAside(string databasePath)
    {
        foreach (var sidecar in Sidecars)
        {
            var from = databasePath + sidecar;
            if (!File.Exists(from))
                continue;

            try
            {
                File.Move(from, databasePath + SetAsideSuffix + sidecar, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing to be done about it here, and the retry will say so.
            }
        }
    }

    public StoredSnapshot Load()
    {
        var resources = new List<StoredResource>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT type, id, json FROM resources";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                resources.Add(new StoredResource(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var outbox = new List<OutboxCommand>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT seq, uuid, type, temp_id, args, prior, attempts, no_verdict, state, last_error FROM outbox ORDER BY seq";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                outbox.Add(new OutboxCommand
                {
                    Seq = reader.GetInt64(0),
                    Uuid = reader.GetString(1),
                    Type = reader.GetString(2),
                    TempId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ArgsJson = reader.GetString(4),
                    PriorJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Attempts = reader.GetInt32(6),
                    NoVerdictRounds = reader.GetInt32(7),
                    State = (OutboxState)reader.GetInt32(8),
                    LastError = reader.IsDBNull(9) ? null : reader.GetString(9),
                });
            }
        }

        var deferred = new List<ResourceKey>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT type, id FROM deferred_deletes";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                deferred.Add(new ResourceKey(reader.GetString(0), reader.GetString(1)));
        }

        return new StoredSnapshot
        {
            SyncToken = ReadSyncToken(),
            Resources = resources,
            Outbox = outbox,
            DeferredDeletes = deferred,
        };
    }

    public void SaveDeferredDeletes(IReadOnlyList<ResourceKey> keys)
    {
        using var tx = _conn.BeginTransaction();

        using (var clear = _conn.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM deferred_deletes";
            clear.ExecuteNonQuery();
        }

        foreach (var key in keys)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO deferred_deletes (type, id) VALUES ($type, $id) ON CONFLICT(type, id) DO NOTHING";
            cmd.Parameters.AddWithValue("$type", key.Type);
            cmd.Parameters.AddWithValue("$id", key.Id);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void SaveSync(IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes, string syncToken)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var u in upserts)
            UpsertResource(u.Type, u.Id, u.Json, tx);
        foreach (var d in deletes)
            DeleteResource(d.Type, d.Id, tx);
        SetSyncToken(syncToken, tx);
        tx.Commit();
    }

    public long ApplyLocalWrite(OutboxCommand command, IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes)
    {
        using var tx = _conn.BeginTransaction();

        foreach (var u in upserts)
            UpsertResource(u.Type, u.Id, u.Json, tx);
        foreach (var d in deletes)
            DeleteResource(d.Type, d.Id, tx);

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO outbox (uuid, type, temp_id, args, prior, attempts, no_verdict, state, last_error)
                VALUES ($uuid, $type, $temp, $args, $prior, $attempts, $noVerdict, $state, $error)
                """;
            cmd.Parameters.AddWithValue("$uuid", command.Uuid);
            cmd.Parameters.AddWithValue("$type", command.Type);
            cmd.Parameters.AddWithValue("$temp", (object?)command.TempId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$args", command.ArgsJson);
            cmd.Parameters.AddWithValue("$prior", (object?)command.PriorJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attempts", command.Attempts);
            cmd.Parameters.AddWithValue("$noVerdict", command.NoVerdictRounds);
            cmd.Parameters.AddWithValue("$state", (int)command.State);
            cmd.Parameters.AddWithValue("$error", (object?)command.LastError ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        using (var query = _conn.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT last_insert_rowid()";
            command.Seq = (long)(query.ExecuteScalar() ?? 0L);
        }

        tx.Commit();
        return command.Seq;
    }

    public void PutResource(string type, string id, string json) => UpsertResource(type, id, json, null);

    public void DeleteResource(string type, string id) => DeleteResource(type, id, null);

    public void RenameResource(string type, string oldId, string newId)
    {
        using var tx = _conn.BeginTransaction();

        string? json;
        using (var read = _conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT json FROM resources WHERE type = $type AND id = $old";
            read.Parameters.AddWithValue("$type", type);
            read.Parameters.AddWithValue("$old", oldId);
            json = read.ExecuteScalar() as string;
        }

        if (json is not null)
        {
            // Delete-then-upsert rather than UPDATE: the new id may already exist, which would
            // violate the primary key and abort the sync mid-reconcile.
            DeleteResource(type, oldId, tx);
            UpsertResource(type, newId, json, tx);
        }

        tx.Commit();
    }

    public void UpdateCommand(OutboxCommand command)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE outbox SET args = $args, attempts = $attempts, no_verdict = $noVerdict, state = $state, last_error = $error WHERE seq = $seq";
        cmd.Parameters.AddWithValue("$args", command.ArgsJson);
        cmd.Parameters.AddWithValue("$attempts", command.Attempts);
        cmd.Parameters.AddWithValue("$noVerdict", command.NoVerdictRounds);
        cmd.Parameters.AddWithValue("$state", (int)command.State);
        cmd.Parameters.AddWithValue("$error", (object?)command.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seq", command.Seq);
        cmd.ExecuteNonQuery();
    }

    public void DeleteCommands(IReadOnlyList<string> uuids)
    {
        if (uuids.Count == 0)
            return;
        using var tx = _conn.BeginTransaction();
        foreach (var uuid in uuids)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM outbox WHERE uuid = $uuid";
            cmd.Parameters.AddWithValue("$uuid", uuid);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Purge()
    {
        using (var tx = _conn.BeginTransaction())
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM resources; DELETE FROM outbox; DELETE FROM meta; DELETE FROM deferred_deletes;";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // Compact outside the transaction so freed pages are released rather than reused in place.
        Execute("VACUUM;");
        Checkpoint();
    }

    public void Dispose()
    {
        try
        {
            Checkpoint();
        }
        catch (SqliteException)
        {
            // Best effort: a failed checkpoint must not prevent the connection from closing.
        }
        _conn.Dispose();
    }

    private void Checkpoint() => Execute("PRAGMA wal_checkpoint(TRUNCATE);");

    private void UpsertResource(string type, string id, string json, SqliteTransaction? tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO resources (type, id, json) VALUES ($type, $id, $json) ON CONFLICT(type, id) DO UPDATE SET json = excluded.json";
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.ExecuteNonQuery();
    }

    private void DeleteResource(string type, string id, SqliteTransaction? tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM resources WHERE type = $type AND id = $id";
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private string ReadSyncToken()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", SyncTokenKey);
        return cmd.ExecuteScalar() as string ?? "*";
    }

    private void SetSyncToken(string token, SqliteTransaction tx)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$key", SyncTokenKey);
        cmd.Parameters.AddWithValue("$value", token);
        cmd.ExecuteNonQuery();
    }

    private void Execute(string sql) => Execute(_conn, sql);

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
