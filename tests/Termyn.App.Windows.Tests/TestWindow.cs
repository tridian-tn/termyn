using Termyn.Core.Capture;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Core.Update;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// A whole main window, over an account with nothing in it.
/// </summary>
/// <remarks>
/// The shell it needs is eight interfaces, and the record that carries them always said tests could
/// stand in for them. Here rather than in whichever suite happened to want one first: a second copy
/// of these stand-ins would be a second thing to keep in step with the interfaces.
/// </remarks>
internal static class TestWindow
{
    /// <summary>One for the run. Nothing here reaches the network; the check just wants a client.</summary>
    private static readonly HttpClient Http = new();

    /// <summary>
    /// Builds a window and lays it out, without showing it.
    /// </summary>
    /// <param name="settingsName">A file name of its own, so two suites can't share one</param>
    /// <returns>The window, which the caller disposes</returns>
    internal static MainForm Build(string settingsName)
    {
        var engine = new SyncEngine(new FakeApi(), new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" });
        engine.Load();

        var clock = new SystemClock();
        var presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
        var scheduler = new SyncScheduler(presenter.SyncAsync, SyncCadence.Default);

        var shell = new Shell(
            new Paths(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), settingsName)),
            new AppSettings(),
            new Hotkey(),
            new AutoStart(),
            new Notifier(),
            new Instance(),
            new GitHubReleaseCheck(Http));

        var window = new MainForm(presenter, scheduler, shell);

        // Laid out on creation rather than on being shown, so what a test measures is what the user
        // would see.
        window.CreateControl();
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
