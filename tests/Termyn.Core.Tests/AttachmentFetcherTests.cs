using Termyn.Core.Api;
using Termyn.Core.Attachments;
using Termyn.Core.Model;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// Fetching a comment's file on request.
/// </summary>
/// <remarks>
/// This is the whole of the offline-first exception, so what matters most is that every outcome
/// which isn't Ready is an ordinary answer with something to say. Offline, not having the file is
/// the expected state of most files most of the time — never a failure to report as one.
/// </remarks>
public sealed class AttachmentFetcherTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "termyn-fetch-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Where a saved copy goes, standing in for the Downloads folder.</summary>
    private readonly string _downloads =
        Path.Combine(Path.GetTempPath(), "termyn-fetch-tests", Guid.NewGuid().ToString("N") + "-downloads");

    private readonly FakeApi _api = new() { FileBytes = [1, 2, 3, 4, 5, 6, 7, 8] };
    private readonly FakeSecrets _secrets = new() { Stored = "tok" };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        if (Directory.Exists(_downloads))
            Directory.Delete(_downloads, recursive: true);
        else if (System.IO.File.Exists(_downloads))
            System.IO.File.Delete(_downloads);
    }

    private AttachmentFetcher Fetcher() => new(_api, _secrets, new AttachmentCache(_directory));

    private static FileAttachment File(bool pending = false, string url = "https://files.example/a", string name = "agenda.pdf")
        => new(name, 8, "application/pdf", url, pending);

    // ---- When it works ---------------------------------------------------------------------------

    [Fact]
    public async Task A_file_is_fetched_and_can_then_be_opened()
    {
        var result = await Fetcher().FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.NotNull(result.Path);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await System.IO.File.ReadAllBytesAsync(result.Path!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Asking_twice_only_goes_to_the_server_once()
    {
        // The point of the cache: reopening the same file is free, and a hundred-megabyte
        // attachment is not fetched again every time somebody clicks it.
        var fetcher = Fetcher();
        await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);
        await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Single(_api.Downloaded);
    }

    [Fact]
    public async Task The_running_total_is_reported_so_the_wait_can_be_shown()
    {
        // The one place in Termyn where the user deliberately waits on the network, so it has to be
        // able to say so.
        // A bag rather than a list, as the download's own test uses: progress is raised on the
        // thread pool, so a report can still be landing while the assert below reads what's here,
        // and a list read mid-add throws.
        var seen = new System.Collections.Concurrent.ConcurrentBag<long>();
        await Fetcher().FetchAsync(File(), new Progress<long>(seen.Add), TestContext.Current.CancellationToken);

        // Given a moment to land before reading it.
        for (var i = 0; i < 50 && seen.IsEmpty; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.NotEmpty(seen);
    }

    [Fact]
    public async Task Whether_it_is_already_here_can_be_asked_without_fetching_it()
    {
        var fetcher = Fetcher();
        Assert.False(fetcher.IsHeld(File()));

        await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.True(fetcher.IsHeld(File()));
    }

    // ---- When it doesn't -------------------------------------------------------------------------

    [Fact]
    public async Task Offline_it_says_so_and_says_it_will_keep()
    {
        _api.TransferThrow = new TodoistNetworkException("offline");

        var result = await Fetcher().FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Offline, result.Outcome);
        Assert.Null(result.Path);
        Assert.Contains("agenda.pdf", result.Message);
        Assert.Contains("back online", result.Message);
    }

    [Fact]
    public async Task Offline_it_leaves_nothing_behind_that_would_later_look_like_the_file()
    {
        _api.TransferThrow = new TodoistNetworkException("offline");
        var fetcher = Fetcher();

        await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.False(fetcher.IsHeld(File()));
        Assert.True(
            !Directory.Exists(_directory) || Directory.GetFiles(_directory).Length == 0,
            "a failed download left something in the cache");
    }

    [Fact]
    public async Task A_server_that_answered_and_said_no_is_not_reported_as_being_offline()
    {
        // "You're offline, it'll be there later" is simply untrue when the connection is fine and
        // Todoist returned a 500 — and it tells the user to wait for something that won't happen.
        _api.TransferThrow = new TodoistNetworkException("Todoist returned HTTP 500.", 500);

        var result = await Fetcher().FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("back online", result.Message);
    }

    [Fact]
    public async Task A_file_the_server_is_still_processing_is_not_fetched_at_all()
    {
        var result = await Fetcher().FetchAsync(File(pending: true), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Pending, result.Outcome);
        Assert.Empty(_api.Downloaded);
        Assert.Contains("still being processed", result.Message);
    }

    [Fact]
    public async Task An_attachment_naming_no_file_says_so_rather_than_trying()
    {
        var result = await Fetcher().FetchAsync(File(url: string.Empty), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Missing, result.Outcome);
        Assert.Empty(_api.Downloaded);
    }

    [Fact]
    public async Task Signed_out_is_an_offline_answer_rather_than_an_error()
    {
        _secrets.Stored = null;

        var result = await Fetcher().FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Offline, result.Outcome);
        Assert.Empty(_api.Downloaded);
    }

    [Fact]
    public async Task A_rejected_token_is_reported_rather_than_read_as_being_offline()
    {
        // Worth telling apart: one comes back on its own when the network does, and the other needs
        // the user to do something about it.
        _api.TransferThrow = new TodoistAuthException("rejected");

        var result = await Fetcher().FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.Contains("token", result.Message);
    }

    [Fact]
    public async Task Calling_a_download_off_leaves_no_half_file()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _api.TransferThrow = new OperationCanceledException(cancelled.Token);

        var fetcher = Fetcher();
        var result = await fetcher.FetchAsync(File(), ct: cancelled.Token);

        Assert.Equal(FetchOutcome.Cancelled, result.Outcome);
        Assert.False(fetcher.IsHeld(File()));
    }

    [Fact]
    public async Task A_download_that_times_out_is_said_rather_than_taken_for_being_called_off()
    {
        // The client's own deadline cancels the read, and the cancellation looks like the user's
        // own. Taken for it, a stalled download simply stopped with nothing said.
        _api.TransferThrow = new TaskCanceledException("the deadline passed");

        var fetcher = Fetcher();
        var result = await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Offline, result.Outcome);
        Assert.Contains("agenda.pdf", result.Message);
        Assert.False(fetcher.IsHeld(File()));
    }

    [Fact]
    public async Task A_save_that_times_out_is_said_as_well()
    {
        _api.TransferThrow = new TaskCanceledException("the deadline passed");

        var result = await Fetcher().SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Offline, result.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(_downloads));
    }

    [Fact]
    public async Task A_cache_folder_that_cannot_be_made_is_an_answer_rather_than_a_crash()
    {
        // A file where the cache's folder should be. Opened outside the try, this came out of the
        // fetch as an exception, and the window waits on it from a handler nothing else watches.
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        await System.IO.File.WriteAllBytesAsync(_directory, [0], TestContext.Current.CancellationToken);

        try
        {
            var result = await Fetcher().FetchAsync(File(), ct: TestContext.Current.CancellationToken);

            Assert.Equal(FetchOutcome.Failed, result.Outcome);
            Assert.Contains("agenda.pdf", result.Message);
        }
        finally
        {
            System.IO.File.Delete(_directory);
        }
    }

    [Theory]
    [MemberData(nameof(Unexpected))]
    public async Task Whatever_the_transfer_throws_comes_back_as_an_answer(Exception thrown)
    {
        // A url the client can't use throws whatever HttpClient makes of it, none of which a
        // transfer is otherwise expected to throw.
        _api.TransferThrow = thrown;

        var fetcher = Fetcher();
        var fetched = await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);
        var saved = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Failed, fetched.Outcome);
        Assert.Equal(FetchOutcome.Failed, saved.Outcome);
        Assert.Contains("agenda.pdf", fetched.Message);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Empty(Directory.GetFileSystemEntries(_downloads));
    }

    public static TheoryData<Exception> Unexpected() =>
    [
        new InvalidOperationException("An invalid request URI was provided."),
        new UriFormatException("Invalid URI: The format of the URI could not be determined."),
        new NotSupportedException("The 'ftp' scheme is not supported."),
        new ArgumentException("Only 'http' and 'https' schemes are allowed."),
    ];

    // ---- Holding a download to its size ----------------------------------------------------------

    [Fact]
    public async Task A_download_running_well_past_its_size_is_stopped_and_leaves_nothing()
    {
        // The comment says eight bytes. A server that keeps sending is stopped well short of
        // filling the disk, and nothing half-written is left to look like the file.
        _api.FileBytes = new byte[200 * 1024];

        var fetcher = Fetcher();
        var result = await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.Contains("larger than Todoist said", result.Message);
        Assert.False(fetcher.IsHeld(File()));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task A_save_running_well_past_its_size_leaves_nothing_in_the_folder()
    {
        _api.FileBytes = new byte[200 * 1024];

        var result = await Fetcher().SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(_downloads));
    }

    [Fact]
    public async Task A_download_a_little_over_its_size_is_still_taken()
    {
        // A size that's been rounded somewhere isn't a reason to refuse the file.
        _api.FileBytes = new byte[12];

        var result = await Fetcher().FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
    }

    [Theory]
    [InlineData(8L, 8L + (64 * 1024))]
    [InlineData(10L * 1024 * 1024, 11L * 1024 * 1024)]
    [InlineData(0L, 1024L * 1024 * 1024)]
    [InlineData(-1L, 1024L * 1024 * 1024)]
    [InlineData(50L * 1024 * 1024 * 1024, 1024L * 1024 * 1024)]
    public void What_a_download_may_come_to_follows_its_size_up_to_a_ceiling(long claimed, long allowed)
        => Assert.Equal(allowed, AttachmentFetcher.Allowance(claimed));

    // ---- Marking what's downloaded ---------------------------------------------------------------

    [Fact]
    public async Task A_fetched_file_is_marked_as_downloaded_from_where_it_came()
    {
        var marker = new Marker();
        var fetcher = new AttachmentFetcher(_api, _secrets, new AttachmentCache(_directory), marker);

        var result = await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal([(result.Path!, "https://files.example/a")], marker.Marked);
    }

    [Fact]
    public async Task A_saved_copy_is_marked_whether_it_was_downloaded_or_copied_out_of_the_cache()
    {
        // A copy read out of the cache is a new file, and the cached one's mark doesn't come with it.
        var marker = new Marker();
        var fetcher = new AttachmentFetcher(_api, _secrets, new AttachmentCache(_directory), marker);

        var downloaded = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);
        await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);
        var copied = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Contains((downloaded.Path!, "https://files.example/a"), marker.Marked);
        Assert.Contains((copied.Path!, "https://files.example/a"), marker.Marked);
    }

    [Fact]
    public async Task A_marker_that_throws_does_not_cost_the_file()
    {
        var fetcher = new AttachmentFetcher(_api, _secrets, new AttachmentCache(_directory), new Marker { Throws = true });

        var result = await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.True(System.IO.File.Exists(result.Path));
    }

    [Fact]
    public async Task Nothing_is_marked_when_the_download_fails()
    {
        var marker = new Marker();
        _api.TransferThrow = new TodoistNetworkException("offline");

        await new AttachmentFetcher(_api, _secrets, new AttachmentCache(_directory), marker)
            .FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Empty(marker.Marked);
    }

    /// <summary>Notes every file it's asked to mark.</summary>
    private sealed class Marker : Termyn.Core.Platform.IDownloadMarker
    {
        public List<(string Path, string? Source)> Marked { get; } = [];

        public bool Throws { get; init; }

        public void Mark(string path, string? sourceUrl)
        {
            if (Throws)
                throw new IOException("no streams here");

            lock (Marked)
                Marked.Add((path, sourceUrl));
        }
    }

    // ---- Keeping the cache in bounds --------------------------------------------------------------

    [Fact]
    public async Task A_download_sweeps_rather_than_waiting_for_the_next_start()
    {
        // This download is exactly what may have put the cache over its cap, so the sweep happens
        // here rather than only when the app is next opened. Two eight-byte files against a cap of
        // twelve: the second is what puts it over, and the first is the older of the two.
        var now = DateTimeOffset.UtcNow;
        var cache = new AttachmentCache(_directory, new CacheLimits(12, TimeSpan.FromDays(14)), () => now);
        var fetcher = new AttachmentFetcher(_api, _secrets, cache);

        await fetcher.FetchAsync(File(url: "https://files.example/a"), ct: TestContext.Current.CancellationToken);
        now = now.AddMinutes(1);
        await fetcher.FetchAsync(File(url: "https://files.example/b"), ct: TestContext.Current.CancellationToken);

        Assert.True(cache.Size() <= 12, $"the cache holds {cache.Size()} bytes against a cap of 12");
    }

    [Fact]
    public async Task A_file_bigger_than_the_whole_cap_is_still_there_once_fetched()
    {
        // The sweep that follows a download mustn't take the file that was just asked for. It's
        // about to be opened or saved, and a path to nothing fails with no reason anyone can see.
        var cache = new AttachmentCache(_directory, new CacheLimits(4, TimeSpan.FromDays(14)));
        var fetcher = new AttachmentFetcher(_api, _secrets, cache);

        var result = await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.True(System.IO.File.Exists(result.Path), "the file was swept away before it could be opened");
    }

    // ---- Saving a copy ---------------------------------------------------------------------------

    [Fact]
    public async Task A_saved_copy_has_the_name_and_the_bytes_it_has_on_the_account()
    {
        var result = await Fetcher().SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.Equal(Path.Combine(_downloads, "agenda.pdf"), result.Path);
        Assert.Equal(_api.FileBytes, await System.IO.File.ReadAllBytesAsync(result.Path!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_name_already_taken_is_never_written_over()
    {
        // Something of the same name was there first: an earlier save, or a file that only shares
        // the name. It keeps its bytes, and each new copy takes the next number.
        Directory.CreateDirectory(_downloads);
        var earlier = Path.Combine(_downloads, "agenda.pdf");
        await System.IO.File.WriteAllBytesAsync(earlier, [9, 9], TestContext.Current.CancellationToken);

        var fetcher = Fetcher();
        var first = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);
        var second = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_downloads, "agenda (1).pdf"), first.Path);
        Assert.Equal(Path.Combine(_downloads, "agenda (2).pdf"), second.Path);
        Assert.Equal(new byte[] { 9, 9 }, await System.IO.File.ReadAllBytesAsync(earlier, TestContext.Current.CancellationToken));
        Assert.Equal(_api.FileBytes, await System.IO.File.ReadAllBytesAsync(second.Path!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_folder_of_the_same_name_counts_as_taken()
    {
        Directory.CreateDirectory(Path.Combine(_downloads, "agenda.pdf"));

        var result = await Fetcher().SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_downloads, "agenda (1).pdf"), result.Path);
    }

    [Theory]
    [InlineData(@"..\..\agenda.pdf")]
    [InlineData("../../agenda.pdf")]
    public async Task A_name_carrying_a_path_is_saved_in_the_folder_all_the_same(string name)
    {
        // Both separators, since a backslash is only a separator on Windows: on macOS and Linux the
        // first is an ordinary name and only the second would climb out of the folder.
        var result = await Fetcher().SaveAsync(File(name: name), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.Equal(_downloads, Path.GetDirectoryName(result.Path));
    }

    [Fact]
    public async Task A_file_already_here_is_saved_without_going_back_to_the_server()
    {
        // Offline included: a file that's been opened once can be saved with no connection at all.
        var fetcher = Fetcher();
        await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);
        _api.TransferThrow = new TodoistNetworkException("offline");

        var result = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.Single(_api.Downloaded);
    }

    [Fact]
    public async Task Offline_it_says_so_and_puts_nothing_in_the_folder()
    {
        _api.TransferThrow = new TodoistNetworkException("offline");

        var result = await Fetcher().SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Offline, result.Outcome);
        Assert.Contains("back online", result.Message);
        Assert.False(Directory.Exists(_downloads) && Directory.EnumerateFileSystemEntries(_downloads).Any(), "something was saved");
    }

    [Fact]
    public async Task A_file_bigger_than_the_whole_cap_can_still_be_saved()
    {
        var fetcher = new AttachmentFetcher(_api, _secrets, new AttachmentCache(_directory, new CacheLimits(4, TimeSpan.FromDays(14))));

        var result = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.Equal(_api.FileBytes, await System.IO.File.ReadAllBytesAsync(result.Path!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_save_called_off_leaves_nothing_in_the_folder()
    {
        // Held already, so it's the copy itself that's called off rather than the download.
        var fetcher = Fetcher();
        await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await fetcher.SaveAsync(File(), _downloads, ct: cancelled.Token);

        Assert.Equal(FetchOutcome.Cancelled, result.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(_downloads));
    }

    [Fact]
    public async Task A_file_not_already_here_is_saved_without_filling_the_cache()
    {
        // Downloaded straight into the folder. Through the cache it would sit on the disk twice.
        var fetcher = Fetcher();

        var result = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.Single(_api.Downloaded);
        Assert.False(fetcher.IsHeld(File()), "the saved file was put in the cache as well");
    }

    [Fact]
    public async Task Saving_a_file_leaves_what_is_already_cached_alone()
    {
        // Eight bytes cached against a cap of twelve. Saved through the cache, a second eight-byte
        // file put it over, and the sweep cleared out the first to make room for a file that was
        // only on its way to Downloads.
        var cache = new AttachmentCache(_directory, new CacheLimits(12, TimeSpan.FromDays(14)));
        var fetcher = new AttachmentFetcher(_api, _secrets, cache);
        await fetcher.FetchAsync(File(url: "https://files.example/a"), ct: TestContext.Current.CancellationToken);

        await fetcher.SaveAsync(File(url: "https://files.example/b"), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.True(fetcher.IsHeld(File(url: "https://files.example/a")), "saving a file swept the cache");
    }

    [Fact]
    public async Task A_download_called_off_part_way_leaves_nothing_in_the_folder()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _api.TransferThrow = new OperationCanceledException(cancelled.Token);

        var result = await Fetcher().SaveAsync(File(), _downloads, ct: cancelled.Token);

        Assert.Equal(FetchOutcome.Cancelled, result.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(_downloads));
    }

    [Fact]
    public async Task A_cached_copy_that_cannot_be_read_is_not_blamed_on_the_folder()
    {
        // Saying it couldn't be saved to Downloads would send the user looking in the wrong place.
        var fetcher = Fetcher();
        var held = (await fetcher.FetchAsync(File(), ct: TestContext.Current.CancellationToken)).Path!;

        FetchResult result;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            result = await fetcher.SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.Contains("downloaded copy", result.Message);
        Assert.DoesNotContain(_downloads, result.Message);
    }

    [Fact]
    public async Task A_save_does_its_work_off_the_thread_that_asked_for_it()
    {
        // Making the folder and the file block, and a Downloads folder on a share that's gone away
        // can hold the thread for as long as Windows takes to give up. The token is read before
        // anything is awaited, so where it's read is where the blocking calls would have run.
        var secrets = new ThreadNoting();
        var fetcher = new AttachmentFetcher(_api, secrets, new AttachmentCache(_directory));
        Task<FetchResult>? save = null;

        var caller = new Thread(() => save = fetcher.SaveAsync(File(), _downloads));
        caller.Start();
        caller.Join();
        await save!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(caller.ManagedThreadId, secrets.ReadOn);
    }

    /// <summary>A token store that notes which thread asked it for the token.</summary>
    private sealed class ThreadNoting : Termyn.Core.Platform.ISecretStore
    {
        public int? ReadOn { get; private set; }

        public string? GetToken()
        {
            ReadOn = Environment.CurrentManagedThreadId;
            return "tok";
        }

        public void SetToken(string token) { }

        public void ClearToken() { }
    }

    [Fact]
    public async Task A_folder_that_cannot_be_made_is_reported_rather_than_thrown()
    {
        // A file sitting where the folder should be.
        Directory.CreateDirectory(Path.GetDirectoryName(_downloads)!);
        await System.IO.File.WriteAllBytesAsync(_downloads, [0], TestContext.Current.CancellationToken);

        var result = await Fetcher().SaveAsync(File(), _downloads, ct: TestContext.Current.CancellationToken);

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.Contains("agenda.pdf", result.Message);
    }
}
