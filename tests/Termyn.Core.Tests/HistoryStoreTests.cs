using Termyn.Core.History;

namespace Termyn.Core.Tests;

/// <summary>
/// The history in a file of its own.
/// </summary>
/// <remarks>
/// Against a real database rather than a fake, since the point of these is the file: that what was
/// written comes back, that it comes back after the process that wrote it has gone, and that the
/// housekeeping takes what it should and nothing else.
/// </remarks>
public class HistoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"termyn-history-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", ".corrupt" })
        {
            var file = _path + suffix;
            if (File.Exists(file))
                File.Delete(file);
        }

        GC.SuppressFinalize(this);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static StoredAction At(int daysAgo, string said) => new(Now.AddDays(-daysAgo), said);

    [Fact]
    public void What_was_written_comes_back_newest_first()
    {
        using var store = new SqliteHistoryStore(_path);

        store.Append([At(3, "Oldest"), At(2, "Middle"), At(1, "Newest")]);

        Assert.Equal(["Newest", "Middle", "Oldest"], store.Recent(10).Select(e => e.Said).ToArray());
    }

    [Fact]
    public void It_is_still_there_after_the_run_that_wrote_it()
    {
        // The whole reason it is a file. Closed and opened again, as a restart would.
        using (var first = new SqliteHistoryStore(_path))
            first.Append([At(1, "Completed something")]);

        using var second = new SqliteHistoryStore(_path);

        Assert.Equal(["Completed something"], second.Recent(10).Select(e => e.Said).ToArray());
    }

    [Fact]
    public void The_time_survives_the_round_trip()
    {
        // Written as an instant and read back as one, so the list can be ordered and swept by it.
        using var store = new SqliteHistoryStore(_path);
        var at = new DateTimeOffset(2026, 8, 15, 9, 30, 15, TimeSpan.Zero);

        store.Append([new StoredAction(at, "Completed something")]);

        Assert.Equal(at, Assert.Single(store.Recent(10)).At);
    }

    [Fact]
    public void Only_as_many_as_were_asked_for()
    {
        using var store = new SqliteHistoryStore(_path);
        store.Append(Enumerable.Range(0, 50).Select(i => At(i, $"Thing {i}")).ToList());

        Assert.Equal(5, store.Recent(5).Count);
        Assert.Empty(store.Recent(0));
    }

    [Fact]
    public void The_sweep_takes_what_is_older_and_leaves_the_rest()
    {
        using var store = new SqliteHistoryStore(_path);
        store.Append([At(40, "Ancient"), At(20, "Old"), At(1, "Recent")]);

        var went = store.Sweep(Now.AddDays(-31));

        Assert.Equal(1, went);
        Assert.Equal(["Recent", "Old"], store.Recent(10).Select(e => e.Said).ToArray());
    }

    [Fact]
    public void A_sweep_with_nothing_to_take_takes_nothing()
    {
        using var store = new SqliteHistoryStore(_path);
        store.Append([At(1, "Recent")]);

        Assert.Equal(0, store.Sweep(Now.AddDays(-31)));
        Assert.Single(store.Recent(10));
    }

    [Fact]
    public void Clearing_it_empties_it()
    {
        using var store = new SqliteHistoryStore(_path);
        store.Append([At(1, "Recent")]);

        store.Clear();

        Assert.Empty(store.Recent(10));
    }

    [Fact]
    public void Writing_nothing_is_not_an_error()
    {
        using var store = new SqliteHistoryStore(_path);

        store.Append([]);

        Assert.Empty(store.Recent(10));
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_stood_aside_rather_than_fatal()
    {
        // The same answer the cache gives. Refusing to start over a list of what you did last week
        // is the worst answer available, and the file is kept because it is the only evidence.
        File.WriteAllText(_path, "this is not a database");

        using (var store = new SqliteHistoryStore(_path))
            store.Append([At(1, "Started again")]);

        Assert.True(File.Exists(_path + ".corrupt"));

        using var reopened = new SqliteHistoryStore(_path);
        Assert.Equal(["Started again"], reopened.Recent(10).Select(e => e.Said).ToArray());
    }
}
