using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Opening and downloading a comment's file, from the comment's menu to where the file ends up, what
/// the status bar says about it, and whether Explorer's asked to show it.
/// </summary>
/// <remarks>
/// The desktop is stood in for, so what's asked of it can be seen without a program or an Explorer
/// window opening on the machine running the tests.
/// </remarks>
public sealed class DownloadAttachmentTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "termyn-download-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Every path Explorer was asked to show, in order.</summary>
    private readonly List<string> _shown = [];

    /// <summary>Every path handed to the desktop to open, in order.</summary>
    private readonly List<string> _opened = [];

    /// <summary>Whether Explorer manages to show what it's asked to.</summary>
    private bool _explorerWorks = true;

    /// <summary>Whether the desktop manages to open what it's handed.</summary>
    private bool _openWorks = true;

    /// <summary>Every name the window asked about before opening, in order.</summary>
    private readonly List<string> _asked = [];

    /// <summary>What the user says when asked whether to open a file that runs something.</summary>
    private bool _sayYes;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private string Downloads => TestWindow.DownloadsIn(_folder);

    /// <summary>A task with one comment on it, which carries a file.</summary>
    /// <param name="name">What the file's called on the account</param>
    private static InMemorySnapshotStore Store(string name)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Home"}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"Write it up","project_id":"p1"}""");
        store.PutResource(
            "notes",
            "n1",
            $$$"""{"id":"n1","item_id":"i1","content":"see attached","file_attachment":{"file_name":"{{{name}}}","file_size":4,"file_url":"https://files.example/a","upload_state":"completed"}}""");
        return store;
    }

    /// <summary>A window on the task, with its comments showing.</summary>
    /// <remarks>
    /// Shown, off the screen, since a task can't be selected in an outline nobody has shown, and
    /// with nothing selected the comments are of nothing.
    /// </remarks>
    /// <param name="settings">The settings to open the window with</param>
    /// <param name="name">What the comment's file is called</param>
    private MainForm Window(AppSettings settings, string name = "agenda.pdf")
    {
        var window = TestWindow.WithDownloads(settings, Store(name), new FakeApi { FileBytes = [1, 2, 3, 4] }, _folder, out var presenter);
        window.ShowInFolder = path =>
        {
            _shown.Add(path);
            return Task.FromResult(_explorerWorks);
        };
        window.OpenFile = path =>
        {
            _opened.Add(path);
            return _openWorks;
        };
        window.AskBeforeRunning = name =>
        {
            _asked.Add(name);
            return _sayYes;
        };

        TestWindow.ShowOffScreen(window);

        presenter.Select(ViewSelection.OfProject("p1"));
        TestWindow.Find<OutlineView>(window).SelectId("i1");
        window.ShowPanelTab(comments: true);
        return window;
    }

    /// <summary>
    /// Picks an entry off the comment's menu, and waits until the window has said something or
    /// done something.
    /// </summary>
    /// <remarks>
    /// Every text the labels take is written down as it's set, rather than read off them afterwards.
    /// The window's first sync is still running when the menu's picked, and its report can write
    /// over the status bar before a look at it would have seen what the download said.
    /// </remarks>
    /// <param name="window">The window to pick in</param>
    /// <param name="entry">The menu entry's text</param>
    /// <param name="done">True once whatever's being waited for has happened</param>
    /// <returns>Every text the window's labels were given meanwhile, in order</returns>
    private static async Task<List<string>> Pick(MainForm window, string entry, Func<List<string>, bool> done)
    {
        var said = new List<string>();
        foreach (var label in TestWindow.Descendants(window).OfType<Label>())
            label.TextChanged += (_, _) => said.Add(label.Text);

        TestWindow.Find<CommentsView>(window).OpenRowMenu()!
            .Items.OfType<ToolStripMenuItem>()
            .Single(i => i.Text == entry)
            .PerformClick();

        // The transfer runs on from the click, and lands back on this thread once it's done.
        for (var i = 0; i < 500 && !done(said); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Application.DoEvents();
        }

        return said;
    }

    /// <summary>Downloads the comment's file, and waits for the status bar to say this.</summary>
    /// <param name="window">The window to download in</param>
    /// <param name="saved">What the status bar should say once it's done</param>
    private static async Task Download(MainForm window, string saved)
    {
        var said = await Pick(window, "Download attachment", said => said.Contains(saved));

        Assert.Contains(saved, said);
    }

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

    [WinFormsFact]
    public async Task Explorer_not_opening_is_said_rather_than_left_looking_like_the_setting_is_off()
    {
        _explorerWorks = false;
        using var window = Window(new AppSettings());

        await Download(window, "Saved agenda.pdf to Downloads, but File Explorer couldn't be opened to show it.");

        Assert.True(File.Exists(Path.Combine(Downloads, "agenda.pdf")), "the file wasn't saved");
    }

    [WinFormsFact]
    public async Task A_file_that_is_not_there_is_not_shown()
    {
        // Answered before any thread is started or Explorer asked, so the caller can say so.
        Assert.False(await AppVersion.ShowInFolder(Path.Combine(_folder, "nothing-here.pdf")));
    }

    [WinFormsFact]
    public async Task Opening_a_file_hands_the_desktop_its_cached_copy_and_saves_nothing()
    {
        using var window = Window(new AppSettings());

        await Pick(window, "Open attachment", _ => _opened.Count > 0);

        var opened = Assert.Single(_opened);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(opened, TestContext.Current.CancellationToken));
        Assert.StartsWith(Path.Combine(_folder, "cache"), opened, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Downloads), "opening a file put a copy in Downloads");
        Assert.Empty(_shown);
    }

    [WinFormsFact]
    public async Task A_document_is_opened_without_asking()
    {
        using var window = Window(new AppSettings());

        await Pick(window, "Open attachment", _ => _opened.Count > 0);

        Assert.Single(_opened);
        Assert.Empty(_asked);
    }

    [WinFormsFact]
    public async Task A_program_is_asked_about_first_and_not_opened_when_the_answer_is_no()
    {
        // A collaborator's "invoice.scr" was one click from running.
        using var window = Window(new AppSettings(), "invoice.exe");

        await Pick(window, "Open attachment", _ => _asked.Count > 0);

        Assert.Equal(["invoice.exe"], _asked);
        Assert.Empty(_opened);
    }

    [WinFormsFact]
    public async Task A_program_is_opened_when_the_answer_is_yes()
    {
        _sayYes = true;
        using var window = Window(new AppSettings(), "invoice.exe");

        await Pick(window, "Open attachment", _ => _opened.Count > 0);

        Assert.Equal(["invoice.exe"], _asked);
        Assert.EndsWith(".exe", Assert.Single(_opened), StringComparison.OrdinalIgnoreCase);
    }

    [WinFormsFact]
    public async Task A_name_dressed_up_as_a_document_is_asked_about_as_what_it_is()
    {
        // Shown as it came, the override makes this read "invoiceexe.pdf".
        using var window = Window(new AppSettings(), "invoice\u202Efdp.exe");

        await Pick(window, "Open attachment", _ => _asked.Count > 0);

        Assert.Equal(["invoicefdp.exe"], _asked);
        Assert.Empty(_opened);
    }

    [WinFormsFact]
    public async Task Saving_a_program_is_not_asked_about()
    {
        // Nothing runs by saving it, and Windows asks for itself if it's later opened from there.
        using var window = Window(new AppSettings(), "invoice.exe");

        await Download(window, "Saved invoice.exe to Downloads.");

        Assert.Empty(_asked);
    }

    [WinFormsFact]
    public async Task A_file_nothing_could_open_is_said_rather_than_thrown()
    {
        _openWorks = false;
        using var window = Window(new AppSettings());

        var said = await Pick(window, "Open attachment", said => said.Contains("agenda.pdf was downloaded but couldn't be opened."));

        Assert.Contains("agenda.pdf was downloaded but couldn't be opened.", said);
    }
}
