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

    /// <summary>Writes something that is the right size to be a database and isn't one.</summary>
    private void WriteRubbish()
    {
        // A real SQLite header, so the file is recognised as a database and then found to be
        // nonsense — which is what "disk image is malformed" means, and is what corruption looks
        // like from the outside. A file of random bytes is refused earlier and by a different path.
        var bytes = new byte[4096];
        "SQLite format 3\0"u8.ToArray().CopyTo(bytes, 0);
        Random.Shared.NextBytes(bytes.AsSpan(16));
        File.WriteAllBytes(Path, bytes);
    }

    [Fact]
    public void A_cache_that_cannot_be_opened_is_started_again_from_nothing()
    {
        WriteRubbish();

        using var store = new SqliteSnapshotStore(Path);

        Assert.True(store.Rebuilt);
        Assert.Empty(store.Load().Resources);
        Assert.Empty(store.Load().Outbox);
    }

    [Fact]
    public void The_rebuilt_cache_works()
    {
        WriteRubbish();

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
        WriteRubbish();
        var original = File.ReadAllBytes(Path);

        using var store = new SqliteSnapshotStore(Path);

        Assert.True(File.Exists(Path + ".corrupt"));
        Assert.Equal(original, File.ReadAllBytes(Path + ".corrupt"));
    }

    [Fact]
    public void A_second_bad_cache_replaces_the_kept_one_rather_than_piling_up()
    {
        // Otherwise a cache that goes bad repeatedly fills somebody's disk with copies of itself.
        WriteRubbish();
        using (var first = new SqliteSnapshotStore(Path))
            Assert.True(first.Rebuilt);

        WriteRubbish();
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
        WriteRubbish();
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
