using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Core.Update;
using Termyn.Core.Capture;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// That a folded task is still folded after a restart.
/// </summary>
/// <remarks>
/// The middle of it, which neither end can see. The presenter can be asked to fold a task and the
/// settings file can be asked to hold a list of ids, and both answer correctly whether or not the
/// window ever carries one to the other — so a wiring that was never done would pass every test on
/// either side and the folds would come back open every morning.
///
/// So these build the window, which is the only thing that reads the state in and writes it back
/// out.
/// </remarks>
public class FoldsAcrossRestartTests : IDisposable
{
    private readonly string _config = Path.Combine(
        Path.GetTempPath(),
        $"termyn-folds-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_config))
            File.Delete(_config);

        GC.SuppressFinalize(this);
    }

    /// <summary>A parent with a child under it, so there is something a fold can hide.</summary>
    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"Parent","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"Child","project_id":"p","parent_id":"a","child_order":1}""");
        return store;
    }

    [Fact]
    public void A_fold_is_written_out_when_the_window_closes()
    {
        using (var window = Window(out var presenter))
        {
            presenter.Select(ViewSelection.Of(SmartView.All));
            presenter.SetCollapsed("a", true);

            window.Close();
        }

        Assert.Contains("\"a\"", Saved());
    }

    [Fact]
    public void A_fold_is_read_back_in_on_the_next_start()
    {
        // Written by hand rather than by the run above, so this stands on its own: what a fresh
        // window does with a file that says a task was folded.
        File.WriteAllText(_config, """{"view":{"collapsedTasks":["a"]}}""");

        using var window = Window(out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        Assert.True(presenter.IsCollapsed("a"));
        Assert.Equal(new[] { "Parent" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    [Fact]
    public void A_fold_survives_the_round_trip_it_is_there_for()
    {
        // Both halves at once, which is the thing the user asked for: fold it, quit, come back.
        using (var window = Window(out var first))
        {
            first.Select(ViewSelection.Of(SmartView.All));
            first.SetCollapsed("a", true);
            window.Close();
        }

        using var second = Window(out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        Assert.Equal(new[] { "Parent" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    [Fact]
    public void A_window_that_was_never_folded_writes_none()
    {
        using (var window = Window(out var presenter))
        {
            presenter.Select(ViewSelection.Of(SmartView.All));
            window.Close();
        }

        // The key may be absent or empty; what matters is that nothing was invented to put in it.
        var saved = Saved();
        Assert.True(
            !saved.Contains("collapsedTasks") || saved.Contains("\"collapsedTasks\": []"),
            $"nothing was folded, so nothing should have been written: {saved}");
    }

    [Fact]
    public void A_file_that_says_nothing_about_folds_opens_everything()
    {
        File.WriteAllText(_config, """{"view":{"sidebarWidth":240}}""");

        using var window = Window(out var presenter);
        presenter.Select(ViewSelection.Of(SmartView.All));

        Assert.Equal(new[] { "Parent", "Child" }, presenter.Rows.Select(r => r.Content).ToArray());
    }

    private string Saved() => File.Exists(_config) ? File.ReadAllText(_config) : string.Empty;

    /// <summary>One for the run. Nothing here reaches the network; the check just wants a client.</summary>
    private static readonly HttpClient Http = new();

    private MainForm Window(out MainPresenter presenter)
    {
        var engine = new SyncEngine(new FakeApi(), Store(), new FakeSecrets { Stored = "tok" });
        engine.Load();

        var clock = new SystemClock();
        presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
        var scheduler = new SyncScheduler(presenter.SyncAsync, SyncCadence.Default);

        var store = new SettingsStore(_config);
        var shell = new Shell(
            new Paths(),
            store,
            store.Load(),
            new Hotkey(),
            new AutoStart(),
            new Notifier(),
            new Instance(),
            new GitHubReleaseCheck(Http));

        var window = new MainForm(presenter, scheduler, shell);

        // Shown, and off the screen while it is. A form that was never displayed doesn't raise its
        // closing event, and that event is where the view state is written — so a window that only
        // realised its handle could never be asked the question these are asking.
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-32000, -32000);
        window.ShowInTaskbar = false;
        window.Show();
        return window;
    }

    // ---- The shell, stood in for --------------------------------------------------------------

    private sealed class Paths : IAppPaths
    {
        public string ConfigDirectory => Path.GetTempPath();
        public string CacheDirectory => Path.GetTempPath();
        public string LogDirectory => Path.GetTempPath();
        public string AttachmentDirectory => Path.GetTempPath();
    }

    private sealed class Hotkey : IGlobalHotkey
    {
        public event Action? Pressed { add { } remove { } }

        public HotkeyBinding? Current => null;
        public bool Register(HotkeyBinding binding) => true;
        public void Unregister() { }
        public void Dispose() { }
    }

    private sealed class AutoStart : IAutoStartService
    {
        public bool IsEnabled => false;
        public bool SetEnabled(bool enabled) => true;
    }

    private sealed class Notifier : INotifier
    {
        public event Action? Activated { add { } remove { } }

        public bool Visible { get; set; }
        public void SetStatus(string tooltip, int dueToday) { }
        public void SetCommands(IReadOnlyList<NotifierCommand> commands) { }
        public void Dispose() { }
    }

    private sealed class Instance : ISingleInstance
    {
        public event Action<string>? SignalReceived { add { } remove { } }

        public bool TryAcquire() => true;
        public bool TrySignal(string message) => true;
        public void Dispose() { }
    }
}
