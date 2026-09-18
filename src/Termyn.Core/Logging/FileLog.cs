using Termyn.Core.Platform;

namespace Termyn.Core.Logging;

/// <summary>
/// A log in a file, rolled by size and capped in number.
/// </summary>
/// <remarks>
/// One line per entry, newest at the bottom of <c>termyn.log</c>. When that file reaches its cap it
/// becomes <c>termyn.1.log</c>, what was <c>termyn.1.log</c> becomes <c>termyn.2.log</c>, and the
/// oldest goes — so the whole log has a ceiling of <see cref="MaxBytes"/> × (<see cref="Kept"/> + 1),
/// plus at most one <see cref="MaxEntry"/> per file, and never has to be tidied by hand.
///
/// Writes are serialised: the sync worker and the window both use this, and interleaved lines would
/// be worse than no lines. Failures are swallowed — a log that can't be written is not a reason to
/// stop working, and a machine with a full disk has enough to deal with.
/// </remarks>
public sealed class FileLog : ILog
{
    /// <summary>How large one file may get before it's rolled.</summary>
    private const long MaxBytes = 512 * 1024;

    /// <summary>How many rolled files are kept beside the current one.</summary>
    private const int Kept = 3;

    /// <summary>
    /// The most one entry may take.
    /// </summary>
    /// <remarks>
    /// The file is rolled on what it already holds, so one entry can always carry it past the cap —
    /// and an exception's text has no length anybody promised. Capping the entry bounds how far
    /// past, which is what keeps the whole log's ceiling a number rather than a hope.
    /// </remarks>
    private const int MaxEntry = 8 * 1024;

    private const string FileName = "termyn.log";

    private readonly string _directory;
    private readonly IClock _clock;
    private readonly Lock _gate = new();

    /// <param name="paths">Where the log directory is</param>
    /// <param name="clock">What stamps each line, so a test can fix it</param>
    public FileLog(IAppPaths paths, IClock? clock = null)
        : this(paths.LogDirectory, clock)
    {
    }

    /// <param name="directory">The directory to write into, made if it isn't there</param>
    /// <param name="clock">What stamps each line, so a test can fix it</param>
    public FileLog(string directory, IClock? clock = null)
    {
        _directory = directory;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>The file being written to now.</summary>
    public string Path => System.IO.Path.Combine(_directory, FileName);

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? error = null) => Write("WARN", message, error);

    public void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    private void Write(string level, string message, Exception? error)
    {
        // One entry, one line. Some of what reaches here is the server's own words, which can carry
        // line breaks of their own, and a message that broke into unindented lines would read as
        // several entries — and would let anything the server says put whatever it liked in the log.
        var line = $"{_clock.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z  {level,-5}  {message.ReplaceLineEndings(" ")}";

        // An exception's own text does run to several lines. They're indented so a reader — and
        // anything counting entries — can tell a continuation from the next entry.
        if (error is not null)
            line += Environment.NewLine + "    " + error.ToString().ReplaceLineEndings(Environment.NewLine + "    ");

        if (line.Length > MaxEntry)
            line = line[..MaxEntry] + " […]";

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                Roll();
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Nothing to be done about it, and nothing worth failing over.
            }
        }
    }

    /// <summary>Moves the current file along when it's full, and drops the oldest. Called with the lock held.</summary>
    private void Roll()
    {
        if (!File.Exists(Path) || new FileInfo(Path).Length < MaxBytes)
            return;

        // The oldest needs no deleting: the move onto it overwrites, which is what drops it.
        for (var i = Kept - 1; i >= 1; i--)
            Move(Archive(i), Archive(i + 1));

        Move(Path, Archive(1));
    }

    private string Archive(int index) => System.IO.Path.Combine(_directory, $"termyn.{index}.log");

    private static void Move(string from, string to)
    {
        if (File.Exists(from))
            File.Move(from, to, overwrite: true);
    }
}
