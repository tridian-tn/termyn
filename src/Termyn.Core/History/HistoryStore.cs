using Microsoft.Data.Sqlite;

namespace Termyn.Core.History;

/// <summary>One thing the user did, as it is kept.</summary>
/// <param name="At">When it was done</param>
/// <param name="Said">The whole of it as a sentence</param>
public readonly record struct StoredAction(DateTimeOffset At, string Said);

/// <summary>
/// Where the list of what has been done is kept between one run and the next.
/// </summary>
/// <remarks>
/// An interface so the list itself needn't know about SQLite, and so a test can watch what it asks
/// for rather than what a file ends up holding.
/// </remarks>
public interface IHistoryStore : IDisposable
{
    /// <summary>Writes entries, oldest first. Called in batches rather than per entry.</summary>
    void Append(IReadOnlyList<StoredAction> entries);

    /// <summary>The most recent entries, newest first.</summary>
    /// <param name="limit">How many at most</param>
    IReadOnlyList<StoredAction> Recent(int limit);

    /// <summary>Throws away everything older than a given moment.</summary>
    /// <returns>How many were removed</returns>
    int Sweep(DateTimeOffset before);

    /// <summary>Empties it.</summary>
    void Clear();
}

/// <summary>
/// The history in a SQLite file of its own.
/// </summary>
/// <remarks>
/// Its own file rather than a table in the cache: the cache is the account's data and is thrown
/// away and rebuilt whenever the server says something surprising, and a record of what the user
/// did should not go with it. It sits in the cache directory all the same, because that is where
/// this app keeps what it holds for itself.
///
/// Nothing here is the account's. Todoist keeps its own activity log; this is a note of what was
/// done from this machine, and losing it costs a convenience rather than any work.
///
/// A file that won't open is stood aside and started again, on the same reasoning the cache uses:
/// refusing to start over a list of what you did last week is the worst answer available.
/// </remarks>
public sealed class SqliteHistoryStore : IHistoryStore
{
    /// <summary>The two SQLite codes that mean the file will never be readable, whatever we do.</summary>
    private const int Corrupt = 11;

    private const int NotADatabase = 26;

    private readonly SqliteConnection _conn;

    public SqliteHistoryStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        try
        {
            _conn = Open(databasePath);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is Corrupt or NotADatabase)
        {
            SetAside(databasePath);
            _conn = Open(databasePath);
        }
    }

    private static SqliteConnection Open(string databasePath)
    {
        // Pooling off, as the cache does: one long-lived connection, and the handle goes promptly.
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

            // Stamped as an ISO-8601 instant, which sorts as text and needs parsing only to be
            // shown. The index is what makes the sweep and the read cheap, and both only ever
            // approach the table from the newest end.
            Execute(conn, """
                CREATE TABLE IF NOT EXISTS entries (seq INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, said TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS entries_at ON entries (at);
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

    /// <summary>Moves a file that couldn't be opened out of the way, sidecars and all.</summary>
    private static void SetAside(string databasePath)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var from = databasePath + suffix;

            try
            {
                if (File.Exists(from))
                    File.Move(from, from + ".corrupt", overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing further to try. Opening will fail again and the app carries on without a
                // history, which is the least of what it does.
            }
        }
    }

    public void Append(IReadOnlyList<StoredAction> entries)
    {
        if (entries.Count == 0)
            return;

        // One transaction for the batch. Written a row at a time this would be a disk sync each,
        // which is the whole reason the caller saves them up.
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO entries (at, said) VALUES ($at, $said);";

        var at = cmd.Parameters.Add("$at", SqliteType.Text);
        var said = cmd.Parameters.Add("$said", SqliteType.Text);

        foreach (var entry in entries)
        {
            at.Value = entry.At.ToString("O");
            said.Value = entry.Said;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<StoredAction> Recent(int limit)
    {
        if (limit <= 0)
            return [];

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT at, said FROM entries ORDER BY seq DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);

        var found = new List<StoredAction>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            // A row whose stamp can't be read is one nothing can be said about, so it is skipped
            // rather than shown at some invented time.
            if (DateTimeOffset.TryParse(reader.GetString(0), out var at))
                found.Add(new StoredAction(at, reader.GetString(1)));
        }

        return found;
    }

    public int Sweep(DateTimeOffset before)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE at < $before;";
        cmd.Parameters.AddWithValue("$before", before.ToString("O"));

        return cmd.ExecuteNonQuery();
    }

    public void Clear() => Execute(_conn, "DELETE FROM entries;");

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Folds the write-ahead log back in, so what is on disk is the whole of it and the sidecars
        // don't outlive the run holding entries nobody would find.
        try
        {
            Execute(_conn, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException)
        {
            // Nothing worth failing a shutdown over; the log is read back on the next open.
        }

        _conn.Dispose();
    }
}
