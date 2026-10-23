using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Download attachment, from the comment's menu to the copy in the folder, what the status bar says
/// about it, and whether Explorer's asked to show it.
/// </summary>
/// <remarks>
/// Explorer's stood in for, so the asking can be seen without a window opening on the machine
/// running the tests.
/// </remarks>
public sealed class DownloadAttachmentTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "termyn-download-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Every path Explorer was asked to show, in order.</summary>
    private readonly List<string> _shown = [];

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private string Downloads => TestWindow.DownloadsIn(_folder);

    /// <summary>A task with one comment on it, which carries a file.</summary>
    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Home"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Write it up","project_id":"p1"}""");
        store.PutResource(
            "notes",
            "n1",
            """{"id":"n1","item_id":"i1","content":"see attached","file_attachment":{"file_name":"agenda.pdf","file_size":4,"file_url":"https://files.example/a","upload_state":"completed"}}""");
        return store;
    }

    /// <summary>A window on the task, with its comments showing.</summary>
    /// <remarks>
    /// Shown, off the screen, since a task can't be selected in an outline nobody has shown, and
    /// with nothing selected the comments are of nothing.
    /// </remarks>
    private MainForm Window(AppSettings settings)
    {
        var window = TestWindow.WithDownloads(settings, Store(), new FakeApi { FileBytes = [1, 2, 3, 4] }, _folder, out var presenter);
        window.ShowInFolder = path =>
        {
            _shown.Add(path);
            return true;
        };

        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-32000, -32000);
        window.ShowInTaskbar = false;
        window.Show();

        presenter.Select(ViewSelection.OfProject("p1"));
        TestWindow.Find<OutlineView>(window).SelectId("i1");
        window.ShowPanelTab(comments: true);
        return window;
    }

    /// <summary>
    /// Picks Download attachment off the comment's menu, and waits for the status bar to say it's
    /// been saved.
    /// </summary>
    /// <param name="window">The window to download in</param>
    /// <param name="saved">What the status bar should end up saying</param>
    private static async Task Download(MainForm window, string saved)
    {
        TestWindow.Find<CommentsView>(window).OpenRowMenu()!
            .Items.OfType<ToolStripMenuItem>()
            .Single(i => i.Text == "Download attachment")
            .PerformClick();

        // The save runs on from the click, and lands back on this thread once the copy's made.
        for (var i = 0; i < 500 && !Says(window, saved); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Application.DoEvents();
        }

        Assert.True(Says(window, saved), $"the status bar never said \"{saved}\"");
    }

    private static bool Says(MainForm window, string text)
        => TestWindow.Descendants(window).OfType<Label>().Any(l => l.Text == text);

    [WinFormsFact]
    public async Task A_downloaded_file_is_shown_in_its_folder()
    {
        using var window = Window(new AppSettings());

        await Download(window, "Saved agenda.pdf to Downloads.");

        var saved = Path.Combine(Downloads, "agenda.pdf");
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken));
        Assert.Equal([saved], _shown);
    }

    [WinFormsFact]
    public async Task With_the_setting_off_the_file_is_saved_and_nothing_opens()
    {
        using var window = Window(new AppSettings { ShowAfterDownloading = false });

        await Download(window, "Saved agenda.pdf to Downloads.");

        Assert.True(File.Exists(Path.Combine(Downloads, "agenda.pdf")), "the file wasn't saved");
        Assert.Empty(_shown);
    }

    [WinFormsFact]
    public async Task Turning_it_off_in_settings_holds_from_the_next_download()
    {
        // Read when the file lands rather than when the window opened, so there's no restart
        // between changing it and it taking effect.
        using var window = Window(new AppSettings());
        window.ApplySettings(new AppSettings { ShowAfterDownloading = false });

        await Download(window, "Saved agenda.pdf to Downloads.");

        Assert.Empty(_shown);
    }

    [WinFormsFact]
    public async Task A_name_the_folder_already_had_is_numbered_in_what_is_said_and_shown()
    {
        // The original name would send the user, and Explorer, to the file that was already there.
        Directory.CreateDirectory(Downloads);
        await File.WriteAllBytesAsync(Path.Combine(Downloads, "agenda.pdf"), [9], TestContext.Current.CancellationToken);
        using var window = Window(new AppSettings());

        await Download(window, "Saved agenda (1).pdf to Downloads.");

        Assert.Equal([Path.Combine(Downloads, "agenda (1).pdf")], _shown);
    }
}
