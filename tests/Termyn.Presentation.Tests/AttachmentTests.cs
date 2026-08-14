using Termyn.Core.Api;
using Termyn.Core.Attachments;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>What the comments pane is told about a file, and what it can ask to be done with one.</summary>
public sealed class AttachmentTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 8, 14);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "termyn-attach-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeApi _api = new() { FileBytes = [1, 2, 3, 4] };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // ---- How a file reads ------------------------------------------------------------------------

    [Theory]
    [InlineData(512, "agenda.pdf (512 B)")]
    [InlineData(2048, "agenda.pdf (2 KB)")]
    [InlineData(5 * 1024 * 1024, "agenda.pdf (5 MB)")]
    public void A_file_is_named_with_its_size_as_somebody_would_say_it(long bytes, string expected)
    {
        var row = new CommentRow("n1", "see attached", "12 Aug", new FileAttachment("agenda.pdf", bytes, "application/pdf", "https://x/a", false));

        Assert.Equal(expected, row.AttachmentLabel);
    }

    [Fact]
    public void One_still_being_processed_says_so_rather_than_giving_a_size()
    {
        var row = new CommentRow("n1", string.Empty, "12 Aug", new FileAttachment("agenda.pdf", 0, string.Empty, string.Empty, true));

        Assert.Equal("agenda.pdf (still processing)", row.AttachmentLabel);
    }

    [Fact]
    public void A_comment_with_no_file_has_nothing_to_say_about_one()
        => Assert.Null(new CommentRow("n1", "words only", "12 Aug", null).AttachmentLabel);

    [Fact]
    public void A_file_reaches_the_pane_from_the_account()
    {
        var presenter = Seeded((
            "notes",
            "n1",
            """{"id":"n1","item_id":"i1","content":"see attached","file_attachment":{"file_name":"a.pdf","file_size":2048,"file_url":"https://files/a","upload_state":"completed"}}"""));

        var row = Assert.Single(presenter.CommentsOn("i1"));
        Assert.Equal("a.pdf", row.Attachment!.FileName);
        Assert.Equal(2048, row.Attachment.FileSize);
        Assert.False(row.Attachment.Pending);
    }

    [Fact]
    public void An_attachment_from_outside_todoist_is_not_read_as_still_processing()
    {
        // Nothing uploaded it, so there is no upload_state on it at all. Reading "not completed" as
        // "not ready" would leave it marked as processing for ever and refuse to open it.
        var presenter = Seeded((
            "notes",
            "n1",
            """{"id":"n1","item_id":"i1","content":"","file_attachment":{"file_name":"a.pdf","file_url":"https://elsewhere/a"}}"""));

        Assert.False(Assert.Single(presenter.CommentsOn("i1")).Attachment!.Pending);
    }

    // ---- Plan limits -----------------------------------------------------------------------------

    [Fact]
    public void A_file_over_what_the_plan_takes_is_refused_before_anything_is_sent()
    {
        var presenter = Seeded(planLimits: """{"current":{"plan_name":"free","upload_limit_mb":5}}""");

        Assert.Equal(5, presenter.UploadLimitMb);
        Assert.True(presenter.AllowsUploadOf(4L * 1024 * 1024));
        Assert.False(presenter.AllowsUploadOf(6L * 1024 * 1024));
    }

    [Fact]
    public void Not_knowing_the_limit_lets_the_server_be_the_one_to_refuse()
    {
        // Unlike reminders, where not knowing has to count as not allowed. Refusing every upload
        // until the first sync lands would make attachments unusable on a fresh install.
        var presenter = Seeded();

        Assert.Null(presenter.UploadLimitMb);
        Assert.True(presenter.AllowsUploadOf(500L * 1024 * 1024));
    }

    // ---- Fetching --------------------------------------------------------------------------------

    [Fact]
    public async Task A_file_is_fetched_and_then_held()
    {
        var presenter = Seeded();
        var file = new FileAttachment("a.pdf", 4, "application/pdf", "https://files/a", false);

        Assert.False(presenter.IsAttachmentHeld(file));

        var result = await presenter.FetchAttachmentAsync(file);

        Assert.Equal(FetchOutcome.Ready, result.Outcome);
        Assert.True(presenter.IsAttachmentHeld(file));
    }

    [Fact]
    public async Task Without_a_download_folder_the_pane_is_told_so_rather_than_left_waiting()
    {
        // A presenter built without a fetcher — which is how most of the tests here run.
        var presenter = Seeded(withFetcher: false);

        var result = await presenter.FetchAttachmentAsync(new FileAttachment("a.pdf", 4, string.Empty, "https://files/a", false));

        Assert.Equal(FetchOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Nothing_is_held_when_there_is_no_file_to_hold()
        => Assert.False(Seeded().IsAttachmentHeld(null));

    // ---- Emptying the cache -----------------------------------------------------------------------

    [Fact]
    public async Task Clearing_the_downloads_takes_them_all()
    {
        var presenter = Seeded();
        await presenter.FetchAttachmentAsync(new FileAttachment("a.pdf", 4, string.Empty, "https://files/a", false));

        Assert.Equal(1, presenter.ClearDownloads());
        Assert.Equal(0, presenter.ClearDownloads());
    }

    [Fact]
    public async Task Tightening_the_caps_sweeps_at_once_rather_than_at_the_next_start()
    {
        var presenter = Seeded();
        await presenter.FetchAttachmentAsync(new FileAttachment("a.pdf", 4, string.Empty, "https://files/a", false));

        presenter.SetDownloadLimits(new CacheLimits(1, TimeSpan.FromDays(14)));

        Assert.False(presenter.IsAttachmentHeld(new FileAttachment("a.pdf", 4, string.Empty, "https://files/a", false)));
    }

    // ---- Setup ------------------------------------------------------------------------------------

    private MainPresenter Seeded(
        params (string Type, string Id, string Json)[] resources)
        => Seeded(null, true, resources);

    private MainPresenter Seeded(string? planLimits = null, bool withFetcher = true, params (string Type, string Id, string Json)[] resources)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Write it up","project_id":"p1"}""");
        store.PutResource("projects", "p1", """{"id":"p1","name":"Home"}""");

        if (planLimits is not null)
            store.PutResource("user_plan_limits", "user_plan_limits", planLimits);

        foreach (var (type, id, json) in resources)
            store.PutResource(type, id, json);

        var secrets = new FakeSecrets { Stored = "tok" };
        var engine = new SyncEngine(_api, store, secrets, new FixedClock(Today));
        engine.Load();

        var fetcher = withFetcher
            ? new AttachmentFetcher(_api, secrets, new AttachmentCache(_directory))
            : null;

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)), fetcher: fetcher);
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
