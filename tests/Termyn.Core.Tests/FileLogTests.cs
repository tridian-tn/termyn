using Termyn.Core.Logging;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// The log file itself: what a line looks like, and what stops it growing for ever.
/// </summary>
/// <remarks>
/// What goes in it is each caller's business (see <see cref="ILog"/>); this is about the file. The
/// one rule it owns is that failing to write is never worth an exception — a machine with a full
/// disk has enough to be going on with.
/// </remarks>
public sealed class FileLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "termyn-log-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private FileLog Log() => new(_directory, new FixedClock(new DateOnly(2026, 9, 18)));

    private string Read() => File.ReadAllText(Path.Combine(_directory, "termyn.log"));

    [Fact]
    public void A_line_says_when_it_was_written_how_bad_it_was_and_what_happened()
    {
        Log().Warn("The cache couldn't be read.");

        var line = Read().Trim();

        Assert.StartsWith("2026-09-18 12:00:00.000Z", line, StringComparison.Ordinal);
        Assert.Contains("WARN", line, StringComparison.Ordinal);
        Assert.EndsWith("The cache couldn't be read.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_goes_under_its_line_rather_than_beside_it()
    {
        // Indented, so a reader and anything counting entries can tell the continuation from the
        // next entry.
        Log().Error("Something went wrong.", new IOException("the disk said no"));

        var lines = Read().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.EndsWith("Something went wrong.", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("    ", lines[1], StringComparison.Ordinal);
        Assert.Contains("the disk said no", string.Join(' ', lines[1..]), StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_written_is_kept_in_order()
    {
        var log = Log();

        log.Info("first");
        log.Warn("second");
        log.Error("third");

        var lines = Read().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.EndsWith("first", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("third", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_file_is_rolled_and_only_so_many_are_kept()
    {
        var log = Log();

        // Each line is about 100 bytes, and the cap is half a megabyte, so this fills several files.
        for (var i = 0; i < 25_000; i++)
            log.Info($"line {i} of a log that is deliberately being made to overflow its cap");

        var files = Directory.GetFiles(_directory).Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(["termyn.1.log", "termyn.2.log", "termyn.3.log", "termyn.log"], files);

        // The whole log has a ceiling, whatever happens: four files, each rolled at half a meg.
        Assert.True(
            Directory.GetFiles(_directory).Sum(f => new FileInfo(f).Length) < 4 * 512 * 1024,
            "the log is over its own ceiling");
    }

    [Fact]
    public void Rolling_keeps_the_newest_and_drops_the_oldest()
    {
        var log = Log();
        log.Info("the oldest line there is");

        // Enough to roll twice over, so the first line should have been pushed out of termyn.1.log
        // and into termyn.2.log rather than staying where it was written.
        for (var i = 0; i < 12_000; i++)
            log.Info($"line {i} of a log that is deliberately being made to overflow its cap");

        Assert.DoesNotContain("the oldest line there is", Read(), StringComparison.Ordinal);
        Assert.Contains(
            "the oldest line there is",
            File.ReadAllText(Path.Combine(_directory, "termyn.2.log")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_cannot_be_written_is_not_worth_an_exception()
    {
        // A path that can't be a directory, standing in for a disk that's full or a folder that
        // isn't ours. Nothing the user was doing should fail because of this.
        var wedged = Path.Combine(_directory, "not-a-directory");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(wedged, "in the way");

        var log = new FileLog(Path.Combine(wedged, "logs"));

        log.Info("this goes nowhere");
        log.Warn("so does this", new IOException("nor this"));
        log.Error("and this");
    }

    [Fact]
    public void The_directory_is_made_on_the_first_line_rather_than_at_the_start()
    {
        // Nothing should leave an empty folder behind for a log it never wrote to.
        var log = Log();
        Assert.False(Directory.Exists(_directory));

        log.Info("here we are");

        Assert.True(File.Exists(Path.Combine(_directory, "termyn.log")));
    }
}
