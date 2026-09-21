using Termyn.Core.Capture;
using Termyn.Core.Logging;
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
    /// <param name="store">What the account holds, or null for an empty one</param>
    /// <returns>The window, which the caller disposes</returns>
    internal static MainForm Build(string settingsName, InMemorySnapshotStore? store = null)
        => Build(settingsName, store, out _, out _);

    /// <summary>
    /// The same window, with the tray it talks to and the presenter behind it.
    /// </summary>
    /// <param name="settingsName">A file name of its own, so two suites can't share one</param>
    /// <param name="store">What the account holds, or null for an empty one</param>
    /// <param name="notifier">The tray, which keeps whatever menu it was given</param>
    /// <param name="presenter">The presenter, for a test that needs to drive it</param>
    /// <returns>The window, which the caller disposes</returns>
    internal static MainForm Build(
        string settingsName,
        InMemorySnapshotStore? store,
        out Notifier notifier,
        out MainPresenter presenter)
    {
        var engine = new SyncEngine(new FakeApi(), store ?? new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" });
        engine.Load();

        var clock = new SystemClock();
        presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
        var scheduler = new SyncScheduler(presenter.SyncAsync, SyncCadence.Default);
        notifier = new Notifier();

        var shell = new Shell(
            new Paths(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), settingsName)),
            new AppSettings(),
            new Hotkey(),
            new AutoStart(),
            notifier,
            new Instance(),
            new GitHubReleaseCheck(Http),
            new RecordingLog());

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

    /// <summary>A tray that keeps the menu it is given, so a test can read it and pick from it.</summary>
    internal sealed class Notifier : INotifier
    {
        public event Action? Activated { add { } remove { } }
        public event Action? MenuOpening;

        /// <summary>The menu as the window last set it.</summary>
        internal IReadOnlyList<NotifierCommand> Commands { get; private set; } = [];

        /// <summary>Raises what the desktop raises as the menu is about to be shown.</summary>
        internal void Opening() => MenuOpening?.Invoke();

        /// <summary>Picks the entry with this label, as a click would.</summary>
        internal void Pick(string label) => Commands.First(c => !c.IsRule && c.Label == label).Invoke();

        public bool Visible { get; set; }
        public void SetStatus(string tooltip, int dueToday) { }
        public void SetCommands(IReadOnlyList<NotifierCommand> commands) => Commands = commands;
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
