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
        => Build(
            new SettingsStore(Path.Combine(Path.GetTempPath(), settingsName)),
            new AppSettings(),
            store ?? new InMemorySnapshotStore(),
            new FakeApi(),
            out notifier,
            out presenter);

    /// <summary>
    /// A window that starts the way the app does, from the settings the last one wrote.
    /// </summary>
    /// <param name="settingsPath">The settings file to read and write, which the caller deletes</param>
    /// <param name="store">What the account holds</param>
    /// <param name="api">The server, for a test that needs to say what a sync brings back</param>
    /// <param name="presenter">The presenter, for a test that needs to drive it</param>
    /// <returns>The window, laid out but not shown, which the caller disposes</returns>
    internal static MainForm Start(
        string settingsPath,
        InMemorySnapshotStore store,
        FakeApi api,
        out MainPresenter presenter)
    {
        var settings = new SettingsStore(settingsPath);
        return Build(settings, settings.Load(), store, api, out _, out presenter);
    }

    private static MainForm Build(
        SettingsStore settingsStore,
        AppSettings settings,
        InMemorySnapshotStore store,
        FakeApi api,
        out Notifier notifier,
        out MainPresenter presenter)
    {
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" });
        engine.Load();

        var clock = new SystemClock();
        presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);
        var scheduler = new SyncScheduler(presenter.SyncAsync, SyncCadence.Default);
        notifier = new Notifier();

        var shell = new Shell(
            new Paths(),
            settingsStore,
            settings,
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

    /// <summary>Every control under a parent, at any depth.</summary>
    /// <param name="parent">Where to start</param>
    /// <returns>Each control below it, parents before their children</returns>
    internal static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;

            foreach (var below in Descendants(child))
                yield return below;
        }
    }

    /// <summary>The one control of a kind somewhere in a window, for a test to drive directly.</summary>
    /// <param name="parent">Where to look</param>
    /// <returns>The control, of which there must be exactly one</returns>
    internal static T Find<T>(Control parent) where T : Control => Descendants(parent).OfType<T>().Single();

    /// <summary>Picks a row in the sidebar, the way a click does.</summary>
    /// <param name="window">The window whose sidebar to click in</param>
    /// <param name="key">The sidebar key of the row to pick</param>
    internal static void Click(MainForm window, string key)
    {
        var tree = Find<TreeView>(window);
        tree.SelectedNode = Nodes(tree.Nodes).Single(n => n.Tag is SidebarNode node && node.Key == key);
    }

    /// <summary>The row the sidebar has lit.</summary>
    /// <param name="window">The window to look in</param>
    /// <returns>Its sidebar key, or null when nothing is selected</returns>
    internal static string? Highlighted(MainForm window)
        => (Find<TreeView>(window).SelectedNode?.Tag as SidebarNode)?.Key;

    /// <summary>Every node in a tree, at any depth.</summary>
    private static IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;

            foreach (var below in Nodes(node.Nodes))
                yield return below;
        }
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
