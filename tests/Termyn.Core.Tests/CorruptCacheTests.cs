using Microsoft.Data.Sqlite;
using Termyn.Core.Sync;

namespace Termyn.Core.Tests;

/// <summary>
/// What the store does with a cache it cannot open.
/// </summary>
/// <remarks>
/// It used to throw out of the constructor, which meant the window never appeared and the only way
/// out was to find a file in %LOCALAPPDATA% and know that deleting it was safe. A cache is a cache:
/// everything in it can be fetched again, and refusing to start is the worst answer available.
/// </remarks>
public class CorruptCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("termyn-cache").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path => System.IO.Path.Combine(_dir, "cache.db");

    /// <summary>
    /// Writes a file that isn't a database at all — SQLITE_NOTADB, "file is not a database".
    /// </summary>
    /// <remarks>
    /// Deterministic, and measured rather than assumed. An earlier version of this scribbled random
    /// bytes over a plausible header and claimed to be producing "disk image is malformed"; it was
    /// producing this instead, and only by luck rather than by design.
    /// </remarks>
    private void WriteNotADatabase()
        => File.WriteAllBytes(Path, "this is definitely not a database"u8.ToArray());

    /// <summary>
    /// Writes a real database with its pages overwritten — SQLITE_CORRUPT, "disk image is malformed".
    /// </summary>
    /// <remarks>
    /// The other way a cache goes bad, and the one the issue was raised for. The header is left
    /// alone so the file is still recognised as a database and only then found to be nonsense.
    /// </remarks>
    private void WriteCorrupted()
    {
        using (var store = new SqliteSnapshotStore(Path))
            for (var i = 0; i < 200; i++)
                store.PutResource("items", $"i{i}", $$"""{"id":"i{{i}}","content":"padding padding padding"}""");

        var bytes = File.ReadAllBytes(Path);
        for (var i = 100; i < bytes.Length; i++)
            bytes[i] = 0x5A;

        File.WriteAllBytes(Path, bytes);
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_started_again_from_nothing()
    {
        WriteNotADatabase();

        using var store = new SqliteSnapshotStore(Path);

        Assert.True(store.Rebuilt);
        Assert.Empty(store.Load().Resources);
        Assert.Empty(store.Load().Outbox);
    }

    [Fact]
    public void A_database_whose_pages_are_corrupt_is_started_again_from_nothing()
    {
        // The one the issue was raised for: a real cache that went bad, rather than a file that was
        // never a cache. Different SQLite code, same answer.
        WriteCorrupted();

        using var store = new SqliteSnapshotStore(Path);

        Assert.True(store.Rebuilt);
        Assert.Empty(store.Load().Resources);
    }

    [Fact]
    public void A_cache_that_is_merely_unavailable_is_not_thrown_away()
    {
        // The important limit. Something else holding the file, a read-only directory, a lock that
        // is about to clear — none of those means the cache is bad, and rebuilding for them would
        // lose the outbox to a problem that was going to resolve itself. It is a worse failure than
        // the one being recovered from, and it is silent.
        using (var store = new SqliteSnapshotStore(Path))
            store.SaveSync([new StoredResource("items", "a", """{"id":"a"}""")], [], "s1");

        using var held = File.Open(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<SqliteException>(() => new SqliteSnapshotStore(Path));
        Assert.False(File.Exists(Path + ".corrupt"));
    }

    [Fact]
    public void A_cache_locked_and_then_released_still_has_everything_in_it()
    {
        // And the point of the limit: the file that was refused is still there afterwards, with the
        // account and anything queued still in it.
        using (var store = new SqliteSnapshotStore(Path))
            store.SaveSync([new StoredResource("items", "a", """{"id":"a"}""")], [], "s1");

        using (var held = File.Open(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<SqliteException>(() => new SqliteSnapshotStore(Path));

        using var reopened = new SqliteSnapshotStore(Path);

        Assert.False(reopened.Rebuilt);
        Assert.Equal("s1", reopened.Load().SyncToken);
        Assert.Single(reopened.Load().Resources);
    }

    [Fact]
    public void The_rebuilt_cache_works()
    {
        WriteNotADatabase();

        using (var store = new SqliteSnapshotStore(Path))
            store.PutResource("items", "a", """{"id":"a"}""");

        using var reopened = new SqliteSnapshotStore(Path);

        Assert.False(reopened.Rebuilt);
        Assert.Equal("""{"id":"a"}""", reopened.Load().Resources.Single().Json);
    }

    [Fact]
    public void The_cache_that_could_not_be_read_is_kept_rather_than_deleted()
    {
        // It is the only evidence of why the app wouldn't start, and somebody may want to look at
        // it. Kept beside the new one rather than in place of it.
        WriteNotADatabase();
        var original = File.ReadAllBytes(Path);

        using var store = new SqliteSnapshotStore(Path);

        Assert.True(File.Exists(Path + ".corrupt"));
        Assert.Equal(original, File.ReadAllBytes(Path + ".corrupt"));
    }

    [Fact]
    public void A_second_bad_cache_replaces_the_kept_one_rather_than_piling_up()
    {
        // Otherwise a cache that goes bad repeatedly fills somebody's disk with copies of itself.
        WriteNotADatabase();
        using (var first = new SqliteSnapshotStore(Path))
            Assert.True(first.Rebuilt);

        WriteNotADatabase();
        var second = File.ReadAllBytes(Path);
        using (var store = new SqliteSnapshotStore(Path))
            Assert.True(store.Rebuilt);

        Assert.Equal(second, File.ReadAllBytes(Path + ".corrupt"));
        Assert.False(File.Exists(Path + ".corrupt.corrupt"));
    }

    [Fact]
    public void A_good_cache_is_left_exactly_where_it_is()
    {
        using (var store = new SqliteSnapshotStore(Path))
            store.SaveSync([new StoredResource("items", "a", """{"id":"a"}""")], [], "s1");

        using var reopened = new SqliteSnapshotStore(Path);

        Assert.False(reopened.Rebuilt);
        Assert.Equal("s1", reopened.Load().SyncToken);
        Assert.Single(reopened.Load().Resources);
        Assert.False(File.Exists(Path + ".corrupt"));
    }

    [Fact]
    public void A_cache_that_was_never_there_is_not_a_rebuild()
    {
        // A first run. Nothing went wrong and nothing was lost, so nothing should be said about it.
        using var store = new SqliteSnapshotStore(Path);

        Assert.False(store.Rebuilt);
    }

    [Fact]
    public void A_stale_write_ahead_log_is_not_read_into_the_cache_that_replaces_it()
    {
        // The risk worth covering: a write-ahead log is read as part of the database it sits beside,
        // so one left behind would put the old contents — and whatever was wrong with them —
        // straight into the file that just replaced it.
        WriteNotADatabase();
        File.WriteAllBytes(Path + "-wal", [1, 2, 3, 4]);
        File.WriteAllBytes(Path + "-shm", [5, 6, 7, 8]);

        using var store = new SqliteSnapshotStore(Path);

        Assert.True(store.Rebuilt);
        Assert.Empty(store.Load().Resources);

        // As it happens SQLite discards sidecars it can't read while failing to open, so by the time
        // the database is moved aside there is usually nothing left to move with it. Written down
        // because the moving code looks unreachable otherwise, and it isn't there for this case — it
        // is there so that a log SQLite did leave doesn't outlive the file it belonged to.
        Assert.False(File.Exists(Path + ".corrupt-wal"));
    }
}
