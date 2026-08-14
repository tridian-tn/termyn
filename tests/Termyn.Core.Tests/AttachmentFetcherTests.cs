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

    private readonly FakeApi _api = new() { FileBytes = [1, 2, 3, 4, 5, 6, 7, 8] };
    private readonly FakeSecrets _secrets = new() { Stored = "tok" };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private AttachmentFetcher Fetcher() => new(_api, _secrets, new AttachmentCache(_directory));

    private static FileAttachment File(bool pending = false, string url = "https://files.example/a")
        => new("agenda.pdf", 8, "application/pdf", url, pending);

    // ---- When it works ---------------------------------------------------------------------------

    [Fact]
    public async Task A_file_is_fetched_and_can_then_be_opened()
    {
        var result = await Fetcher().FetchAsync(File());

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.NotNull(result.Path);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await System.IO.File.ReadAllBytesAsync(result.Path!));
    }

    [Fact]
    public async Task Asking_twice_only_goes_to_the_server_once()
    {
        // The point of the cache: reopening the same file is free, and a hundred-megabyte
        // attachment is not fetched again every time somebody clicks it.
        var fetcher = Fetcher();
        await fetcher.FetchAsync(File());
        await fetcher.FetchAsync(File());

        Assert.Single(_api.Downloaded);
    }

    [Fact]
    public async Task The_running_total_is_reported_so_the_wait_can_be_shown()
    {
        // The one place in Termyn where the user deliberately waits on the network, so it has to be
        // able to say so.
        var seen = new List<long>();
        await Fetcher().FetchAsync(File(), new Progress<long>(seen.Add));

        // Progress is raised on the thread pool, so give it a moment to land before reading it.
        for (var i = 0; i < 50 && seen.Count == 0; i++)
            await Task.Delay(10);

        Assert.NotEmpty(seen);
    }

    [Fact]
    public async Task Whether_it_is_already_here_can_be_asked_without_fetching_it()
    {
        var fetcher = Fetcher();
        Assert.False(fetcher.IsHeld(File()));

        await fetcher.FetchAsync(File());

        Assert.True(fetcher.IsHeld(File()));
    }

    // ---- When it doesn't -------------------------------------------------------------------------

    [Fact]
    public async Task Offline_it_says_so_and_says_it_will_keep()
    {
        _api.TransferThrow = new TodoistNetworkException("offline");

        var result = await Fetcher().FetchAsync(File());

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

        await fetcher.FetchAsync(File());

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

        var result = await Fetcher().FetchAsync(File());

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("back online", result.Message);
    }

    [Fact]
    public async Task A_file_the_server_is_still_processing_is_not_fetched_at_all()
    {
        var result = await Fetcher().FetchAsync(File(pending: true));

        Assert.Equal(FetchOutcome.Pending, result.Outcome);
        Assert.Empty(_api.Downloaded);
        Assert.Contains("still being processed", result.Message);
    }

    [Fact]
    public async Task An_attachment_naming_no_file_says_so_rather_than_trying()
    {
        var result = await Fetcher().FetchAsync(File(url: string.Empty));

        Assert.Equal(FetchOutcome.Missing, result.Outcome);
        Assert.Empty(_api.Downloaded);
    }

    [Fact]
    public async Task Signed_out_is_an_offline_answer_rather_than_an_error()
    {
        _secrets.Stored = null;

        var result = await Fetcher().FetchAsync(File());

        Assert.Equal(FetchOutcome.Offline, result.Outcome);
        Assert.Empty(_api.Downloaded);
    }

    [Fact]
    public async Task A_rejected_token_is_reported_rather_than_read_as_being_offline()
    {
        // Worth telling apart: one comes back on its own when the network does, and the other needs
        // the user to do something about it.
        _api.TransferThrow = new TodoistAuthException("rejected");

        var result = await Fetcher().FetchAsync(File());

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.Contains("token", result.Message);
    }

    [Fact]
    public async Task Calling_a_download_off_leaves_no_half_file()
    {
        using var cancelled = new CancellationTokenSource();
        _api.TransferThrow = new OperationCanceledException(cancelled.Token);

        var fetcher = Fetcher();
        var result = await fetcher.FetchAsync(File(), ct: cancelled.Token);

        Assert.Equal(FetchOutcome.Cancelled, result.Outcome);
        Assert.False(fetcher.IsHeld(File()));
    }

    // ---- Keeping the cache in bounds --------------------------------------------------------------

    [Fact]
    public async Task A_download_sweeps_rather_than_waiting_for_the_next_start()
    {
        // This download is exactly what may have put the cache over its cap, so the sweep happens
        // here rather than only when the app is next opened.
        var cache = new AttachmentCache(_directory, new CacheLimits(4, TimeSpan.FromDays(14)));
        var fetcher = new AttachmentFetcher(_api, _secrets, cache);

        await fetcher.FetchAsync(File());

        Assert.True(cache.Size() <= 4, $"the cache holds {cache.Size()} bytes against a cap of 4");
    }
}
