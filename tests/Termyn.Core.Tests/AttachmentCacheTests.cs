using Termyn.Core.Attachments;

namespace Termyn.Core.Tests;

/// <summary>
/// Where downloaded attachments live, and the rules that stop them living there for ever.
/// </summary>
/// <remarks>
/// Nothing here is authoritative — every file can be fetched again — so the interesting cases are
/// all about it being safe to sweep, and about a half-finished download never being mistaken for a
/// finished one.
/// </remarks>
public sealed class AttachmentCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "termyn-cache-tests", Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private AttachmentCache Cache(long maxBytes = 1024 * 1024, int maxAgeDays = 14)
        => new(_directory, new CacheLimits(maxBytes, TimeSpan.FromDays(maxAgeDays)), () => _now);

    /// <summary>Puts a finished download in the cache, as a fetch would.</summary>
    private string Put(AttachmentCache cache, string url, string name, int bytes)
    {
        var (stream, path) = cache.OpenForWrite(url, name);
        using (stream)
            stream.Write(new byte[bytes]);

        cache.Commit(path);
        return path;
    }

    // ---- Where files go --------------------------------------------------------------------------

    [Fact]
    public void Two_files_of_the_same_name_do_not_collide()
    {
        // Every account has more than one "notes.pdf" in it.
        var cache = Cache();

        Assert.NotEqual(
            cache.PathFor("https://files.example/a", "notes.pdf"),
            cache.PathFor("https://files.example/b", "notes.pdf"));
    }

    [Fact]
    public void The_extension_is_kept_so_the_desktop_knows_what_it_is()
    {
        // Handing a file to the default application is done by extension. A hash with none opens
        // nothing at all.
        Assert.EndsWith(".pdf", Cache().PathFor("https://files.example/a", "agenda.pdf"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agenda.pdf", ".pdf")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("no-extension", "")]
    [InlineData("trouble.pdf\\..\\..\\evil", "")]
    [InlineData("trouble.p df", "")]
    [InlineData("trouble.thisisaverylongextension", "")]
    public void An_extension_from_an_account_is_checked_rather_than_trusted(string fileName, string expected)
    {
        // It arrives from the server, so it is somebody else's text being built into a path here.
        var path = Cache().PathFor("https://files.example/a", fileName);

        Assert.Equal(expected, Path.GetExtension(path));
        Assert.Equal(_directory, Path.GetDirectoryName(path));
    }

    // ---- Hits and misses -------------------------------------------------------------------------

    [Fact]
    public void A_miss_is_an_ordinary_answer_rather_than_a_failure()
        => Assert.Null(Cache().Find("https://files.example/never-fetched", "a.pdf"));

    [Fact]
    public void A_file_that_is_here_is_found()
    {
        var cache = Cache();
        Put(cache, "https://files.example/a", "a.pdf", 10);

        Assert.NotNull(cache.Find("https://files.example/a", "a.pdf"));
    }

    [Fact]
    public void A_download_that_never_finished_is_not_a_hit()
    {
        // The half-written file is beside the real name, not at it. Otherwise a cancelled download
        // leaves a truncated file that every later open would hand to the desktop as the real thing.
        var cache = Cache();
        var (stream, path) = cache.OpenForWrite("https://files.example/a", "a.pdf");
        using (stream)
            stream.Write(new byte[10]);

        Assert.Null(cache.Find("https://files.example/a", "a.pdf"));

        cache.Abandon(path);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    // ---- Sweeping ---------------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_swept_while_it_is_within_both_caps()
    {
        var cache = Cache();
        Put(cache, "https://files.example/a", "a.pdf", 100);

        Assert.Equal(0, cache.Sweep());
        Assert.NotNull(cache.Find("https://files.example/a", "a.pdf"));
    }

    [Fact]
    public void What_has_not_been_wanted_for_long_enough_goes()
    {
        var cache = Cache(maxAgeDays: 14);
        Put(cache, "https://files.example/old", "old.pdf", 10);

        _now = _now.AddDays(15);

        Assert.Equal(1, cache.Sweep());
        Assert.Null(cache.Find("https://files.example/old", "old.pdf"));
    }

    [Fact]
    public void Opening_a_file_again_keeps_it_for_longer()
    {
        // Age is from when it was last wanted, not from when it arrived. Reopening the same file all
        // week should keep it, which age-from-download would not.
        var cache = Cache(maxAgeDays: 14);
        Put(cache, "https://files.example/a", "a.pdf", 10);

        _now = _now.AddDays(10);
        Assert.NotNull(cache.Find("https://files.example/a", "a.pdf"));

        _now = _now.AddDays(10);

        Assert.Equal(0, cache.Sweep());
        Assert.NotNull(cache.Find("https://files.example/a", "a.pdf"));
    }

    [Fact]
    public void Over_the_size_cap_the_least_recently_wanted_go_first()
    {
        var cache = Cache(maxBytes: 250);

        Put(cache, "https://files.example/oldest", "1.bin", 100);
        _now = _now.AddHours(1);
        Put(cache, "https://files.example/middle", "2.bin", 100);
        _now = _now.AddHours(1);
        Put(cache, "https://files.example/newest", "3.bin", 100);

        cache.Sweep();

        Assert.Null(cache.Find("https://files.example/oldest", "1.bin"));
        Assert.NotNull(cache.Find("https://files.example/middle", "2.bin"));
        Assert.NotNull(cache.Find("https://files.example/newest", "3.bin"));
    }

    [Fact]
    public void Sweeping_stops_as_soon_as_it_fits_rather_than_emptying_the_lot()
    {
        var cache = Cache(maxBytes: 250);
        for (var i = 0; i < 4; i++)
        {
            Put(cache, $"https://files.example/{i}", $"{i}.bin", 100);
            _now = _now.AddHours(1);
        }

        cache.Sweep();

        Assert.True(cache.Size() <= 250, $"the cache is {cache.Size()} bytes against a cap of 250");
        Assert.NotEmpty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Emptying_it_takes_everything()
    {
        var cache = Cache();
        Put(cache, "https://files.example/a", "a.pdf", 10);
        Put(cache, "https://files.example/b", "b.pdf", 10);

        Assert.Equal(2, cache.Clear());
        Assert.Equal(0, cache.Size());
    }

    [Fact]
    public void A_cache_that_was_never_written_to_answers_rather_than_throwing()
    {
        // Nothing is downloaded until something is opened, so an account that never opens an
        // attachment has no such folder at all.
        var cache = Cache();

        Assert.Equal(0, cache.Size());
        Assert.Equal(0, cache.Sweep());
        Assert.Equal(0, cache.Clear());
        Assert.Null(cache.Find("https://files.example/a", "a.pdf"));
    }
}
