using System.ComponentModel;
using System.Diagnostics;
using Termyn.Core;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Core.Update;
using Termyn.Presentation;

// Both namespaces have a Label, and in a form file the control is what "Label" should mean.
using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// The platform services the shell is built on, handed in so the window doesn't construct its own
/// and tests of the app could stand in for them.
/// </summary>
/// <param name="StartInTray">
/// Start with no window on screen — how a launch-at-login entry gets Termyn running, and its hotkey
/// live, without taking over what the user was doing.
/// </param>
/// <param name="StartWithQuickAdd">Open the quick-add box straight away, and nothing else.</param>
internal sealed record Shell(
    IAppPaths Paths,
    SettingsStore Store,
    AppSettings Settings,
    IGlobalHotkey Hotkey,
    IAutoStartService AutoStart,
    INotifier Notifier,
    ISingleInstance Instance,
    GitHubReleaseCheck Updates,
    bool StartInTray = false,
    bool StartWithQuickAdd = false,

    /// <summary>
    /// True when the cache on disk couldn't be read and was started again from nothing. Said in the
    /// status bar rather than swallowed: anything queued and unsent went with the old file.
    /// </summary>
    bool CacheRebuilt = false);

/// <summary>Main window: sidebar, capture box, task outline, and the keyboard map.</summary>
internal sealed class MainForm : Form
{
    private const string ReconnectMessage = "Your Todoist token was rejected and cleared. Restart Termyn to reconnect.";

    private readonly MainPresenter _presenter;
    private readonly SyncScheduler _scheduler;
    private readonly Shell _shell;
    private readonly CancellationTokenSource _cts = new();

    private readonly TextBox _capture;
    private readonly Label _preview;
    private readonly TextBox _search;
    private readonly TreeView _sidebar;
    private readonly OutlineView _outline;
    private readonly Label _status;
    private readonly SplitContainer _split;

    /// <summary>The right-click menu on a task, refilled for whichever row it opens over.</summary>
    private readonly ContextMenuStrip _taskMenu;

    /// <summary>Splits the outline from the notes panel under it.</summary>
    private readonly SplitContainer _detail;

    /// <summary>The task's description, in the markdown the account stores it as.</summary>
    private readonly MarkdownEditor _notes;

    /// <summary>The same description, rendered.</summary>
    private readonly MarkdownView _rendered;

    /// <summary>
    /// Whether the notes panel is showing the markdown to type into rather than the rendering.
    /// </summary>
    /// <remarks>
    /// One pane, two things it can be, rather than both at once down a splitter. The panel is nine
    /// lines of a window at the best of times, and a description read beside itself gave each half
    /// of that half the width — while leaving only one of them able to take a keystroke and only
    /// the other able to follow a link.
    /// </remarks>
    private bool _writingNotes;

    /// <summary>Which task the notes box is on and what it was opened with.</summary>
    private readonly DescriptionDraft _draft = new();

    /// <summary>
    /// What the notes box said, so Ctrl+Z can put it back.
    /// </summary>
    /// <remarks>
    /// Ours because the control's own queue can't be used once the text is highlighted: applying a
    /// colour is recorded on it as an action, so Ctrl+Z would un-highlight rather than undo.
    /// </remarks>
    private readonly NotesHistory _history = new();

    /// <summary>
    /// Holds the rendering back until the typing stops. Re-parsing and re-styling on every
    /// keystroke is work nobody is waiting on, and it is done while they are still typing.
    /// </summary>
    private readonly System.Windows.Forms.Timer _renderIdle;

    /// <summary>
    /// Writes the notes once the typing has stopped for a while, so an edit left on screen while
    /// the user goes elsewhere isn't held hostage to them coming back to it.
    /// </summary>
    private readonly System.Windows.Forms.Timer _saveIdle;

    /// <summary>The panel size as the user last left it, which is what gets saved.</summary>
    private int _notesHeight;

    /// <summary>
    /// True while the panels are being sized by us rather than dragged by the user, so the layout
    /// settling doesn't get recorded as a size anybody chose. Starts true: everything up to the
    /// first apply is the window arranging itself.
    /// </summary>
    private bool _adjustingPanels = true;

    /// <summary>Shown in place of a result when the selected filter is beyond the local grammar.</summary>
    private readonly LinkLabel _unsupported;

    /// <summary>
    /// Built once and hidden between uses, so the hotkey has it ready. Made just after the window
    /// appears rather than during startup: the hundred-millisecond budget is on the hotkey, and
    /// building it before the first paint spends that time on every start instead.
    /// </summary>
    private QuickAddForm? _quickAdd;

    private AppSettings _settings;
    private Theme _theme;

    private string? _editingId;
    private string? _editingText;
    private bool _reconnectNeeded;
    private bool _syncingSidebar;
    private bool _exiting;

    /// <summary>The sidebar row the user actually clicked, which the id alone can't identify.</summary>
    private string _sidebarKey = ViewSelection.Default.Key;

    /// <summary>The sidebar last rendered, so an unchanged one isn't rebuilt.</summary>
    private IReadOnlyList<SidebarNode>? _renderedSidebar;

    /// <summary>Branches the user had closed last time, until the first render puts them back.</summary>
    private HashSet<string>? _restoreCollapsed;

    private Font? _headerFont;

    /// <summary>False until the window is up, so the first paint isn't held up drawing a tray icon.</summary>
    private bool _trayReady;

    /// <summary>
    /// What to say about the hotkey, held until the user has something else worth reading. Sticky,
    /// not one-shot: the first render happens before any paint and the sync that follows overwrites
    /// the line immediately, so a one-shot notice was never on screen.
    /// </summary>
    private string? _hotkeyNotice;

    /// <summary>
    /// Said once the window is up, when the cache had to be rebuilt.
    /// </summary>
    /// <remarks>
    /// Held until the user does something, rather than until the next sync. Clearing it on the sync
    /// was tried and is no use: the sync lands a second or two after the window appears, so the one
    /// thing this exists to say — that anything queued and unsent went with the old file — was gone
    /// from the screen before anybody could read it. Measured, not guessed: three seconds after
    /// launch the status bar already said "Synced just now".
    ///
    /// Nothing here can be acted on, so there is nothing to wait for except the user being present.
    /// Touching anything is proof enough of that.
    /// </remarks>
    private string? _cacheNotice;

    /// <summary>Whether the signal subscription is live, so shutdown doesn't unhook what was never hooked.</summary>
    private bool _signalsWired;

    public MainForm(MainPresenter presenter, SyncScheduler scheduler, Shell shell)
    {
        _presenter = presenter;
        _scheduler = scheduler;
        _shell = shell;
        _settings = shell.Settings;
        _theme = Theme.Resolve(_settings.Theme);

        Text = "Termyn";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 400);
        KeyPreview = true;

        _capture = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Add a task…  #project /section @label p1 tomorrow 4pm" };
        _capture.KeyDown += OnCaptureKeyDown;
        _capture.TextChanged += (_, _) => UpdatePreview();

        _preview = new Label { Dock = DockStyle.Top, Height = 20, Padding = new Padding(4, 2, 0, 0) };

        _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search…" };
        _search.TextChanged += (_, _) => Guarded(() => _presenter.Search(_search.Text));

        _sidebar = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            ShowLines = false,
            ShowRootLines = false,
            FullRowSelect = true,
            Indent = 14,
            BorderStyle = BorderStyle.None,
        };
        _sidebar.MouseDown += (_, _) => Noticed();
        _sidebar.AfterSelect += OnSidebarSelect;
        _sidebar.KeyDown += OnSidebarKeyDown;

        _outline = new OutlineView { Dock = DockStyle.Fill };

        // So a row selected while its task still had the name we gave it isn't read as deleted the
        // moment the sync learns what the server calls it.
        _outline.Renamed = _presenter.CurrentIdOf;

        _outline.KeyDown += OnOutlineKeyDown;
        _outline.MouseDown += (_, _) => Noticed();
        _outline.BeforeLabelEdit += OnBeforeLabelEdit;
        _outline.AfterLabelEdit += OnAfterLabelEdit;
        _outline.SortRequested += column => Guarded(() => _presenter.SortBy(column));
        _outline.SelectedIndexChanged += (_, _) => FollowSelection();

        // Empty until it opens, when it is filled for the row it opened over. Assigning it here is
        // also what makes Shift+F10 and the menu key reach it, without either being handled.
        _taskMenu = new ContextMenuStrip();
        _taskMenu.Opening += OnTaskMenuOpening;
        _outline.ContextMenuStrip = _taskMenu;

        _unsupported = new LinkLabel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 4, 8, 4),
            Visible = false,
        };
        _unsupported.LinkClicked += (_, _) => OpenTodoist();

        _notes = new MarkdownEditor
        {
            Dock = DockStyle.Fill,
            Placeholder = "Notes…  **bold**  *italic*  - a list",

            // Until a task is selected there is nowhere for anything typed here to go. Set on the
            // way in because the selection has never changed at this point, so nothing else does.
            ReadOnly = true,
        };
        _notes.TextChanged += OnNotesChanged;
        _notes.KeyDown += OnNotesKeyDown;

        // Reading is where the panel rests, so the focus going elsewhere ends the edit as well as
        // saving it — clicking back into the outline shouldn't leave the markers on show.
        _notes.Leave += (_, _) => StopWriting();

        _rendered = new MarkdownView { Dock = DockStyle.Fill };
        _rendered.LinkOpened += OnNotesLinkOpened;
        _rendered.EditRequested += StartWriting;

        // The notes go under the outline rather than beside it: the outline is five columns wide
        // before it is useful, and a panel down the side of it takes that from the task names.
        _detail = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
        };
        _detail.Panel1.Controls.Add(_outline);

        // Both fill the panel and exactly one of them is visible, which is what makes this one pane
        // rather than two. Which is visible is the only thing deciding what you see — the order
        // they go in doesn't come into it, since the hidden one draws nothing whichever side of the
        // other it sits on.
        _detail.Panel2.Controls.Add(_notes);
        _detail.Panel2.Controls.Add(_rendered);
        _detail.SplitterMoved += (_, _) => RememberPanelSizes();

        // Said here rather than left to the first selection, which may never come: a control that
        // is merely behind another is still in the tab order, so without this the markdown could
        // take the focus while nothing on screen showed it had.
        _notes.Visible = false;

        // One pause, two things that wait for it: the rendering when the panel is reading, and the
        // highlighting and the undo state when it is being typed into.
        _renderIdle = new System.Windows.Forms.Timer { Interval = 300 };
        _renderIdle.Tick += (_, _) =>
        {
            _renderIdle.Stop();

            if (_writingNotes)
            {
                _notes.Restyle();
                _history.Record(_notes.Text, _notes.SelectionStart);
            }
            else
            {
                RenderNotes();
            }
        };

        // Long enough that it isn't saving mid-sentence, short enough that walking away from a
        // half-written note doesn't leave it only in the window. Each save is a queued
        // item_update, which the outbox coalesces on the way out and which — unlike a completion
        // or a delete — records nothing on the undo stack, so a session of these can't push a
        // Ctrl+Z out of reach.
        _saveIdle = new System.Windows.Forms.Timer { Interval = 5000 };
        _saveIdle.Tick += (_, _) => SaveNotes();

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
        };
        _split.Panel1.Controls.Add(_sidebar);
        _split.Panel2.Controls.Add(_detail);

        // Above the outline, so it reads as an explanation of the empty list below it.
        _split.Panel2.Controls.Add(_unsupported);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Text = "Loading…",
        };

        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(_search);
        Controls.Add(_preview);
        Controls.Add(_capture);

        // Last, so it docks outermost and sits above the capture box where a menu bar belongs.
        // Built after the outline and the sidebar exist: filling it asks both what is selected.
        var bar = BuildMenuBar();
        MainMenuStrip = bar;
        Controls.Add(bar);

        _headerFont = new Font(_sidebar.Font, FontStyle.Bold);

        RestoreViewState(_settings.View);

        // Only the colours here. Loading publishes and renders a moment later anyway, and rendering
        // the whole outline twice before the window is even up is time the user waits for.
        ApplyTheme(render: false);

        _presenter.RowsChanged += OnRowsChanged;
        _presenter.StatusChanged += OnStatusChanged;
        _scheduler.SyncFailed += OnSyncFailed;
        _shell.Hotkey.Pressed += OnHotkey;
        _shell.Notifier.Activated += OnTrayActivated;

        BuildTrayMenu();
        _hotkeyNotice = RegisterHotkey(announce: false);

        if (shell.CacheRebuilt)
        {
            _cacheNotice = "The local cache could not be read and has been rebuilt. "
                + "Anything not yet synced was lost; the old file is kept beside it.";
        }

        Load += async (_, _) => await LoadAsync();
        Shown += OnShown;
        FormClosing += OnFormClosing;

        // The notes box losing the focus to another control raises Leave; the whole window losing
        // it to another application does not — the box stays the window's active control and takes
        // the focus back on return. Without this, alt-tabbing away from a half-written note left it
        // sitting in the window: not queued, so not on the phone, and not there at all if the
        // process went without closing.
        Deactivate += (_, _) => SaveNotes();
        FormClosed += (_, _) =>
        {
            _presenter.RowsChanged -= OnRowsChanged;
            _presenter.StatusChanged -= OnStatusChanged;
            _scheduler.SyncFailed -= OnSyncFailed;
            _shell.Hotkey.Pressed -= OnHotkey;
            _shell.Notifier.Activated -= OnTrayActivated;
            if (_signalsWired)
                _shell.Instance.SignalReceived -= OnInstanceSignal;

            _renderIdle.Dispose();
            _saveIdle.Dispose();
            _quickAdd?.AllowClose();
            _quickAdd?.Dispose();
            _headerFont?.Dispose();
            _taskMenu.Dispose();
            _cts.Dispose();
        };
    }

    // ---- Shell ---------------------------------------------------------------------------------

    /// <summary>
    /// Closing leaves Termyn in the tray when that is what the user asked for, because the global
    /// hotkey only works while the process is alive. Exit from the tray menu closes for real.
    /// </summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Before the state is written either way: closing to the tray leaves the window standing,
        // but it is still the moment the user stopped working in the notes box.
        SaveNotes();

        if (!_exiting && _settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SaveViewState();
            _shell.Notifier.Visible = true;
            Hide();
            return;
        }

        SaveViewState();
        _cts.Cancel();
    }

    /// <summary>
    /// Drops straight to the tray when this launch wasn't meant to show a window. Done on Shown
    /// rather than before: the form has to be realised for the outline to have painted anything, and
    /// hiding it earlier leaves the first restore with nothing on it.
    /// </summary>
    private void OnShown(object? sender, EventArgs e)
    {
        // Painted and taking input, which is what the startup budget is measured to.
        StartupTrace.Interactive(_shell.Paths, _presenter.Rows.Count);

        // Posted rather than run here, so it happens after this paint has been processed: the first
        // tray icon costs the best part of a tenth of a second, and none of it is work the user is
        // waiting on.
        BeginInvoke(() =>
        {
            _trayReady = true;
            RenderTray();
            _shell.Notifier.Visible = true;
            _ = QuickAdd; // built now, so the first hotkey press finds it ready

            // Subscribed here rather than in the constructor: anything a second launch signalled
            // during startup is handed over the moment this runs, and OnUi would have dropped it
            // while the window still had no handle.
            _signalsWired = true;
            _shell.Instance.SignalReceived += OnInstanceSignal;

            if (!_shell.StartInTray)
                return;

            Hide();

            if (_shell.StartWithQuickAdd)
                Guarded(QuickAdd.Summon);
        });
    }

    /// <summary>The quick-add box, built on first use.</summary>
    private QuickAddForm QuickAdd
    {
        get
        {
            if (_quickAdd is not null)
                return _quickAdd;

            _quickAdd = new QuickAddForm(_presenter, _theme);
            _quickAdd.Captured += () => _scheduler.NotifyWrite();
            _quickAdd.Failed += ex => Report(ex);
            return _quickAdd;
        }
    }

    /// <summary>
    /// Runs work on the UI thread, dropping it if the window has gone.
    /// </summary>
    /// <remarks>
    /// Every event source here — the hotkey, the tray, a second launch, the sync loop, the presenter
    /// — can fire from another thread or after the form is disposed. The guard used to be written
    /// out at each of them, and omitted at the tray menu altogether.
    /// </remarks>
    private void OnUi(Action work)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        if (InvokeRequired)
            BeginInvoke(work);
        else
            work();
    }

    private void OnHotkey() => OnUi(() => Guarded(QuickAdd.Summon));

    private void OnTrayActivated() => OnUi(RestoreWindow);

    private void OnInstanceSignal(string message) => OnUi(() => Guarded(() =>
    {
        if (message == InstanceSignals.QuickAdd)
            QuickAdd.Summon();
        else
            RestoreWindow();
    }));

    private void RestoreWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void BuildTrayMenu() => _shell.Notifier.SetCommands(
    [
        new NotifierCommand("Open Termyn", () => OnUi(RestoreWindow)),
        new NotifierCommand("Quick add…", () => OnUi(() => Guarded(QuickAdd.Summon))),
        new NotifierCommand("Sync now", () => _scheduler.RequestNow()),
        new NotifierCommand("Settings…", () => OnUi(() => Guarded(OpenSettings))),
        new NotifierCommand("Check for updates…", () => OnUi(() => _ = CheckForUpdatesAsync())),
        new NotifierCommand("Exit", () => OnUi(Exit)),
    ]);

    private void Exit()
    {
        _exiting = true;
        Close();
        Application.Exit();
    }

    /// <summary>Takes the global hotkey.</summary>
    /// <returns>What to tell the user, or null when there is nothing worth saying.</returns>
    private string? RegisterHotkey(bool announce)
    {
        if (!_settings.HotkeyEnabled)
        {
            _shell.Hotkey.Unregister();
            return announce ? "Quick-add has no hotkey." : null;
        }

        var binding = _settings.HotkeyBinding;
        if (_shell.Hotkey.Register(binding))
            return announce ? $"Quick-add hotkey is {binding}." : null;

        // Silence here would leave the user pressing a key that does nothing, with no way to tell
        // that something else on the machine already owns it. Said whether or not this was a change.
        return $"Another application already owns {binding} — quick-add has no hotkey.";
    }

    /// <summary>
    /// Asks whether a newer Termyn has been published, and offers to open its page.
    /// </summary>
    /// <remarks>
    /// Manual, as the spec has it for v1: nothing checks on a timer, nothing downloads, and nothing
    /// about the account leaves the machine — this is a GET for a version number.
    /// </remarks>
    private async Task CheckForUpdatesAsync()
    {
        _status.Text = "Checking for updates…";

        await GuardedAsync(async () =>
        {
            var advice = (await _shell.Updates.LatestAsync(_cts.Token)).Advise(AppVersion.Current);

            if (IsDisposed)
                return;

            _status.Text = advice.Message;

            // Nothing to open means nothing to ask about — there is no update, or we never found
            // out. The status bar has said so, but only if there is a window to say it on: launched
            // with --tray, or closed to the tray, this was invoked from a menu attached to nothing
            // the user can see, and the commonest answer of all — "you're on the latest" — would
            // leave the menu item looking broken.
            if (advice.OpenUrl is not { } url)
            {
                if (!Visible || WindowState == FormWindowState.Minimized)
                    MessageBox.Show(advice.Message, "Termyn", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var answer = MessageBox.Show(
                Visible ? this : null,
                advice.Message + "\r\n\r\nOpen the release page?",
                "Termyn",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);

            if (answer == DialogResult.OK && !AppVersion.OpenLink(url))
                _status.Text = "Couldn't open the release page.";
        });
    }

    private void ShowAbout()
        => MessageBox.Show(
            this,
            $"Termyn {AppVersion.Tag}\r\n\r\nA keyboard-driven Todoist client for Windows.\r\n\r\n"
            + $"Installed in:\r\n{AppVersion.Location}\r\n\r\n"
            + $"Settings and token:\r\n{_shell.Paths.ConfigDirectory}\r\n\r\n"
            + $"Cache and logs:\r\n{_shell.Paths.CacheDirectory}",
            "About Termyn",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

    private void OpenSettings()
    {
        if (SettingsForm.Edit(this, _settings, _theme) is not { } amended)
            return;

        var themeChanged = amended.Theme != _settings.Theme;
        var hotkeyChanged = amended.HotkeyEnabled != _settings.HotkeyEnabled
                            || amended.HotkeyBinding != _settings.HotkeyBinding;
        var cadenceChanged = amended.SyncMode != _settings.SyncMode
                             || amended.ClampedInterval != _settings.ClampedInterval;

        // Collected rather than each written straight to the label: four of these can happen in one
        // save, and the last to run used to be the only one the user saw — including the one saying
        // their launch-at-login change had been refused and quietly put back.
        var notices = new List<string>();

        if (amended.LaunchAtLogin != _settings.LaunchAtLogin && !_shell.AutoStart.SetEnabled(amended.LaunchAtLogin))
        {
            // Left where it was rather than saved as a wish the OS didn't grant.
            amended = amended with { LaunchAtLogin = _settings.LaunchAtLogin };
            notices.Add("Windows would not change the launch-at-login setting.");
        }

        _settings = amended;

        if (themeChanged)
        {
            _theme = Theme.Resolve(_settings.Theme);
            ApplyTheme();
            _quickAdd?.ApplyTheme(_theme);

            // The framework's own chrome — scrollbars, menus, title bars — is set once at startup.
            notices.Add("Restart Termyn for the window frame to follow the theme.");
        }

        if (hotkeyChanged)
        {
            // Cleared either way: whatever it said is about the binding that has just been replaced.
            _hotkeyNotice = null;
            if (RegisterHotkey(announce: true) is { } hotkeyNotice)
                notices.Add(hotkeyNotice);
        }

        if (cadenceChanged)
            notices.Add("The sync cadence takes effect when Termyn next starts.");

        // Saved last, so the theme and hotkey are applied even when the file can't be written.
        if (!SaveViewState())
            notices.Add("Settings could not be written to disk.");

        if (notices.Count > 0)
            _status.Text = string.Join("  ·  ", notices);
    }

    private void ApplyTheme(bool render = true)
    {
        _theme.Apply(this);
        _outline.Theme = _theme;
        _rendered.Theme = _theme;
        _notes.Theme = _theme;
        _preview.ForeColor = _theme.Muted;
        _sidebar.BackColor = _theme.Background;
        _outline.BackColor = _theme.Panel;
        _renderedSidebar = null; // header colours are set per node, so the tree has to be rebuilt
        Invalidate(invalidateChildren: true);

        if (render)
            Render();
    }

    // ---- View state ----------------------------------------------------------------------------

    private void RestoreViewState(ViewState state)
    {
        // Only remembered here. The splitters are set once the window has a size to divide, which
        // is not yet: before it is parented a SplitContainer clamps every distance it is given
        // against its own default and quietly sticks there.
        _notesHeight = state.DescriptionHeight;

        // _adjustingPanels is still true here, so the layout this sets off doesn't record the
        // container's unparented default over what has just been read.
        _detail.Panel2Collapsed = !state.ShowDescription;

        // Outer bounds throughout, matching what CurrentViewState saves. Saving the outer size and
        // restoring it as the client size grew the window by the frame on every maximise-and-exit
        // cycle, compounding until it walked off the screen.
        var size = new Size(
            Math.Max(state.WindowWidth, MinimumSize.Width),
            Math.Max(state.WindowHeight, MinimumSize.Height));

        if (state is { WindowX: { } x, WindowY: { } y })
        {
            var bounds = new Rectangle(new Point(x, y), size);

            // A monitor that has since been unplugged would otherwise put the window out of reach.
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Location = bounds.Location;
            }
        }

        Size = size;
        if (state.Maximized)
            WindowState = FormWindowState.Maximized;

        // Given to the presenter, not just to the tree: highlighting the saved row while the outline
        // showed Today was the opposite of remembering where the user was. It can refuse — the
        // project may have been deleted elsewhere — in which case the key is still worth holding, so
        // the row is picked up if a sync brings it back.
        if (state.SelectedKey is { Length: > 0 } key)
        {
            _presenter.SelectByKey(key);
            _sidebarKey = key;
        }

        _restoreCollapsed = state.CollapsedKeys.Count > 0
            ? state.CollapsedKeys.ToHashSet(StringComparer.Ordinal)
            : null;
    }

    private ViewState CurrentViewState()
    {
        // The restored bounds, not the current ones, when maximised or minimised: those report the
        // maximised frame, which would become the window's size the next time it was un-maximised.
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

        return new ViewState
        {
            SelectedKey = _sidebarKey,
            CollapsedKeys = CollapsedKeys().ToList(),
            SidebarWidth = _split.SplitterDistance,
            ShowDescription = !_detail.Panel2Collapsed,
            DescriptionHeight = _notesHeight,
            WindowX = bounds.X,
            WindowY = bounds.Y,
            WindowWidth = bounds.Width,
            WindowHeight = bounds.Height,
            Maximized = WindowState == FormWindowState.Maximized,
        };
    }

    /// <summary>Writes the settings and the current window state.</summary>
    /// <returns>False when the file couldn't be written.</returns>
    private bool SaveViewState()
    {
        _settings = _settings with { View = CurrentViewState() };
        return _shell.Store.Save(_settings);
    }

    // ---- Rendering -----------------------------------------------------------------------------

    private void OnRowsChanged() => OnUi(Render);

    /// <summary>
    /// Only the status moved. Kept apart from a full render so a sync starting and finishing doesn't
    /// rebuild the outline twice, and doesn't disturb a row being renamed.
    /// </summary>
    private void OnStatusChanged() => OnUi(RenderStatus);

    private void RenderStatus()
    {
        if (IsDisposed)
            return;

        if (_reconnectNeeded)
        {
            _status.Text = ReconnectMessage;
            _status.ForeColor = Theme.ForPriority(Priority.P1);
            return;
        }

        // Ahead of the hotkey's notice, and sticky for the same reason: this one says something was
        // lost, and the sync that follows the first render would otherwise replace it unseen.
        if (_cacheNotice is { } cacheNotice)
        {
            _status.Text = cacheNotice;
            _status.ForeColor = _theme.Accent;
            return;
        }

        // Held until the hotkey is next set, rather than shown once: the first render runs before
        // any paint, and the sync that immediately follows would replace it unseen.
        if (_hotkeyNotice is { } notice)
        {
            _status.Text = notice;
            _status.ForeColor = _theme.Accent;
            return;
        }

        _status.Text = _presenter.Status;

        // Coloured by state, which is why the presenter hands back a status rather than a sentence:
        // offline and reconnect are worth noticing, and everything else is not.
        _status.ForeColor = _presenter.SyncStatus.State switch
        {
            SyncState.Offline or SyncState.Paused => _theme.Accent,
            SyncState.ReconnectNeeded => Theme.ForPriority(Priority.P1),
            _ => _theme.Muted,
        };
    }

    private void Render()
    {
        // A background sync must not wipe the row the user is currently renaming.
        if (IsDisposed || _editingId is not null)
            return;

        RenderSidebar();

        // Before the rows, so the header's arrow and the order beneath it are put up together.
        _outline.Ordering = _presenter.Sort;
        _outline.Rows = _presenter.Rows;

        // After the rows, which is what decides whether the selected task is still there.
        FollowSelection();
        RenderStatus();
        RenderUnsupported();
        RenderTray();
    }

    /// <summary>
    /// Keeps the tray icon in step: the count it badges is today's, which the sidebar has already
    /// worked out as part of the same pass.
    /// </summary>
    private void RenderTray()
    {
        if (!_trayReady)
            return;

        var today = _presenter.DueToday;

        var tooltip = today switch
        {
            0 => "Termyn — nothing due today",
            1 => "Termyn — 1 task due today",
            _ => $"Termyn — {today} tasks due today",
        };

        _shell.Notifier.SetStatus(tooltip, today);
    }

    private void RenderUnsupported()
    {
        if (_presenter.UnsupportedFilter is not { } query)
        {
            _unsupported.Visible = false;
            return;
        }

        const string link = "Open in Todoist";
        _unsupported.Text = $"Termyn can't read this filter: {query}\r\n{link}";
        _unsupported.LinkArea = new LinkArea(_unsupported.Text.Length - link.Length, link.Length);
        _unsupported.Visible = true;
    }

    private void OpenTodoist()
    {
        // The saved filter lives in the account, so the app's own filter page is where to land.
        Guarded(() => AppVersion.OpenLink(Links.TodoistFilters));
    }

    private void RenderSidebar()
    {
        // Nothing the tree shows has changed — a search keystroke, or a click, which republishes
        // the same rows because nothing in the sidebar depends on which one is selected. Identity
        // only answers the cheapest case: a publish builds a fresh list every time, so the contents
        // are what has to be compared. Rebuilding a tree that already matches is what made clicking
        // a row jump the scroll — clearing the nodes drops the view to the top, and the selection
        // that follows scrolls back down to the row that was clicked.
        if (_renderedSidebar is { } rendered
            && (ReferenceEquals(rendered, _presenter.Sidebar) || rendered.SequenceEqual(_presenter.Sidebar)))
        {
            return;
        }

        // Where the tree is scrolled to and what it's sitting on, so a rebuild that doesn't move
        // the selection — a sync changing a count, say — doesn't move the viewport either.
        var top = SidebarKeyOf(_sidebar.TopNode);
        var selected = SidebarKeyOf(_sidebar.SelectedNode);

        _renderedSidebar = _presenter.Sidebar;
        _syncingSidebar = true;
        try
        {
            // Remember which branches are closed; the rebuild would otherwise reopen them. On the
            // first render there is nothing to read yet, so the saved set stands in.
            var collapsed = _restoreCollapsed ?? CollapsedKeys();
            _restoreCollapsed = null;

            _sidebar.BeginUpdate();
            _sidebar.Nodes.Clear();

            // The presenter hands back a flattened tree; rebuild the nesting from each row's depth.
            var parents = new Dictionary<int, TreeNode>();
            foreach (var node in _presenter.Sidebar)
            {
                var label = node.Count > 0 ? $"{node.Label}  ({node.Count})" : node.Label;
                var tree = new TreeNode(label) { Tag = node };

                if (node.Kind == SidebarKind.Header)
                {
                    tree.NodeFont = _headerFont;
                    tree.ForeColor = _theme.Muted;
                }

                if (node.Depth > 0 && parents.TryGetValue(node.Depth - 1, out var parent))
                    parent.Nodes.Add(tree);
                else
                    _sidebar.Nodes.Add(tree);

                parents[node.Depth] = tree;

                // Match the exact row that was clicked: a favourited project appears twice.
                if (node.Key == _sidebarKey)
                    _sidebar.SelectedNode = tree;
            }

            _sidebar.ExpandAll();
            Recollapse(_sidebar.Nodes, collapsed);

            // The selected row may have gone — deleted here, or removed by a sync. Fall back to
            // whatever the presenter is actually showing rather than highlighting nothing.
            if (_sidebar.SelectedNode is null)
            {
                _sidebarKey = _presenter.Selection.Key;
                _sidebar.SelectedNode = FindByKey(_sidebar.Nodes, _sidebarKey);
            }
        }
        finally
        {
            _sidebar.EndUpdate();
            _syncingSidebar = false;
        }

        // After EndUpdate, not inside it: with redraw suppressed the control doesn't scroll where
        // it's told. Only when the selection stayed put — if it moved, it has already scrolled
        // itself into view and that is where the user should be looking.
        if (top is not null
            && selected == SidebarKeyOf(_sidebar.SelectedNode)
            && FindByKey(_sidebar.Nodes, top) is { } was)
        {
            _sidebar.TopNode = was;
        }
    }

    private static string? SidebarKeyOf(TreeNode? node) => (node?.Tag as SidebarNode)?.Key;

    private HashSet<string> CollapsedKeys()
    {
        var collapsed = new HashSet<string>(StringComparer.Ordinal);
        Walk(_sidebar.Nodes);
        return collapsed;

        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                if (node is { IsExpanded: false, Nodes.Count: > 0 } && node.Tag is SidebarNode tagged)
                    collapsed.Add(tagged.Key);
                Walk(node.Nodes);
            }
        }
    }

    private static void Recollapse(TreeNodeCollection nodes, HashSet<string> collapsed)
    {
        foreach (TreeNode node in nodes)
        {
            Recollapse(node.Nodes, collapsed);
            if (node.Tag is SidebarNode tagged && collapsed.Contains(tagged.Key))
                node.Collapse();
        }
    }

    private static TreeNode? FindByKey(TreeNodeCollection nodes, string key)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is SidebarNode tagged && tagged.Key == key)
                return node;
            if (FindByKey(node.Nodes, key) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// A background sync threw. Routed through the same reporting as everything else, so a
    /// cancellation raised inside a sync is swallowed here too rather than painted as a failure.
    /// </summary>
    private void OnSyncFailed(Exception ex) => OnUi(() => Report(ex, "Background sync failed: "));

    // ---- Navigation ----------------------------------------------------------------------------

    private void OnSidebarSelect(object? sender, TreeViewEventArgs e)
    {
        if (_syncingSidebar || e.Node?.Tag is not SidebarNode node)
            return;

        // A group label isn't a view; leave the outline where it was.
        if (node.Kind == SidebarKind.Header)
            return;

        _sidebarKey = node.Key;

        // By key, not by selection: a favourited project is two rows, and telling the presenter only
        // which view to open would leave it believing the copy down in the tree was the one clicked.
        Guarded(() => _presenter.SelectByKey(node.Key));
    }

    /// <summary>
    /// Moves to the next or previous view — Ctrl+↑/↓ from anywhere in the window, so switching views
    /// doesn't need the sidebar to have focus first. The presenter owns which row is next, since it
    /// owns the sidebar's order.
    /// </summary>
    private void SwitchView(int offset)
    {
        if (!_presenter.SelectAdjacent(offset))
            return;

        _sidebarKey = _presenter.SelectedKey;
        Highlight();
    }

    private void GoTo(ViewSelection selection)
    {
        _sidebarKey = selection.Key;
        Guarded(() => _presenter.Select(selection));
        Highlight();
    }

    /// <summary>
    /// Moves the tree's highlight to the current row with the select handler suppressed, so
    /// navigating from the palette or a hotkey publishes once rather than twice.
    /// </summary>
    private void Highlight()
    {
        _syncingSidebar = true;
        try
        {
            _sidebar.SelectedNode = FindByKey(_sidebar.Nodes, _sidebarKey);
        }
        finally
        {
            _syncingSidebar = false;
        }
    }

    private static IEnumerable<TreeNode> Flatten(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Nodes))
                yield return child;
        }
    }

    private void OnSidebarKeyDown(object? sender, KeyEventArgs e)
    {
        var command = CommandFor(e.KeyData, Scope.Sidebar);
        if (command == AppCommand.None)
            return;

        // The same rule the Organise menu greys by, asked of the same place: a smart view isn't
        // ours to rename, and a section has no star to take off.
        var node = _sidebar.SelectedNode?.Tag as SidebarNode;
        if (!Presentation.Commands.StateOf(command, new CommandContext(Selection: node)).Enabled)
            return;

        e.Handled = true;
        e.SuppressKeyPress = true;
        Run(command);
    }

    /// <returns>True when the rename went ahead</returns>
    private bool RenameStructure(SidebarNode node)
    {
        var name = InputDialog.Ask(this, "Rename", "New name:", node.Label);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        Guarded(() =>
        {
            switch (node.Kind)
            {
                case SidebarKind.Project:
                    _presenter.RenameProject(node.Id, name);
                    break;
                case SidebarKind.Section:
                    _presenter.RenameSection(node.Id, name);
                    break;
                case SidebarKind.Label:
                    _presenter.RenameLabel(node.Id, name);
                    break;
            }
        });

        return true;
    }

    /// <returns>True when the delete went ahead</returns>
    private bool DeleteStructure(SidebarNode node)
    {
        // A label delete takes the label off its tasks; the other two take the tasks with them.
        var question = node.Kind == SidebarKind.Label
            ? $"Delete the label \"{node.Label}\" and remove it from every task?"
            : $"Delete the {(node.Kind == SidebarKind.Project ? "project" : "section")} \"{node.Label}\" and everything in it?";

        var answer = MessageBox.Show(this, question, "Termyn", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK)
            return false;

        Guarded(() =>
        {
            switch (node.Kind)
            {
                case SidebarKind.Project:
                    _presenter.DeleteProject(node.Id);
                    break;
                case SidebarKind.Section:
                    _presenter.DeleteSection(node.Id);
                    break;
                case SidebarKind.Label:
                    _presenter.DeleteLabel(node.Id);
                    break;
            }
        });

        return true;
    }

    // ---- Capture -------------------------------------------------------------------------------

    private async void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
            return;

        e.SuppressKeyPress = true;
        var text = _capture.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;

        _capture.Clear();
        UpdatePreview();

        if (!await GuardedAsync(() => _presenter.CaptureAsync(text, _cts.Token)))
        {
            // Nothing was created anywhere, so give the user their typing back.
            _capture.Text = text;
            _capture.SelectionStart = text.Length;
            UpdatePreview();
            return;
        }

        _scheduler.NotifyWrite();
    }

    private void UpdatePreview() => _preview.Text = _presenter.PreviewText(_capture.Text);

    // ---- Notes ---------------------------------------------------------------------------------

    /// <summary>
    /// Puts the notes box on whichever task the outline is now on, writing anything typed into the
    /// one it was on before.
    /// </summary>
    /// <remarks>
    /// Called from the outline's selection and from every render, which is why it does nothing when
    /// the task hasn't actually changed: a sync republishes the rows every forty-five seconds, and
    /// each of those reassignments moves the native selection whether or not the user did.
    /// </remarks>
    private void FollowSelection()
    {
        var id = _outline.SelectedId;
        if (id == _draft.TaskId)
        {
            RefreshNotes();
            return;
        }

        // The same task under a new name: created here a moment ago, and the sync has just learned
        // what the server calls it. Followed rather than reopened, because reopening would replace
        // what is being typed with what the account holds — which for a task that new is nothing.
        if (id is not null && _draft.TaskId is { } held && _presenter.CurrentIdOf(held) == id)
        {
            _draft.Retarget(id);
            RefreshNotes();
            return;
        }

        // The task under the box is changing, so whatever was typed into it belongs to the old one
        // and has to go before the box is refilled.
        SaveNotes();

        _draft.Open(id, _presenter.DescriptionOf(id));
        ShowNotes(_draft.Opened);

        // ReadOnly rather than Enabled: disabling a control that has the focus hands the focus to
        // the next one and never gives it back, so a sync arriving mid-sentence used to leave the
        // user typing into the outline — where space ticks a task off and delete removes it.
        //
        // And read-only for a completed task pulled out of the archive: those are held apart from
        // the live model, so an edit to one is declined rather than queued, and a box that took the
        // typing and dropped it would be worse than one that doesn't take it.
        _notes.ReadOnly = !_presenter.HasTask(id);

        // A task with no notes on it has nothing to read, and a rendering of nothing is a blank
        // panel that gives no sign it would take any typing. So an empty one opens ready to write.
        SetNotesMode(writing: _draft.Opened.Length == 0 && !_notes.ReadOnly, focus: false);
    }

    /// <summary>
    /// Takes a republished description, unless it would land on top of something being typed.
    /// </summary>
    private void RefreshNotes()
    {
        if (_draft.TaskId is not { } id || !_draft.CanRefresh(_notes.Text))
            return;

        var current = _presenter.DescriptionOf(id);
        if (current == DescriptionDraft.Normalised(_notes.Text))
            return;

        // Changed elsewhere while the box sat open and untouched — on the web, or by an undo.
        _draft.Open(id, current);
        ShowNotes(current);
    }

    /// <summary>Fills the box without it counting as something the user typed.</summary>
    private void ShowNotes(string text)
    {
        _notes.TextChanged -= OnNotesChanged;
        try
        {
            // A rich edit control holds a line ending as the single newline the account stores,
            // so what goes in is what came out of the account and the offsets agree throughout.
            _notes.Text = text;
            _notes.Restyle();

            // Nothing before this belongs to this task. Without it, Ctrl+Z on a note you have just
            // opened replaces it with the previous task's.
            _history.Reset(text);
        }
        finally
        {
            _notes.TextChanged += OnNotesChanged;
        }

        _renderIdle.Stop();
        _saveIdle.Stop();
        RenderNotes();
    }

    /// <summary>
    /// Draws the notes as they read, if there is anywhere to draw them.
    /// </summary>
    /// <remarks>
    /// Asked rather than assumed, because the wait for the typing to stop outlives the panel: close
    /// it a moment after typing and the tick still arrives, to parse and style a description into a
    /// control nobody can see. Everything that puts the panel back on screen renders as it does so,
    /// which is what keeps skipping it here from leaving a stale rendering behind.
    /// </remarks>
    private void RenderNotes()
    {
        if (!_detail.Panel2Collapsed && !_writingNotes)
            _rendered.Markdown = _notes.Text;
    }

    /// <summary>
    /// Puts the panel into writing or reading, and draws whichever of the two it has become.
    /// </summary>
    /// <param name="writing">True for the markdown, false for the rendering</param>
    /// <param name="focus">Whether the pane being shown should also be given the keyboard</param>
    private void SetNotesMode(bool writing, bool focus)
    {
        _writingNotes = writing;

        _notes.Visible = writing;
        _rendered.Visible = !writing;

        // Anything typed while the panel was reading — which is nothing, but also anything the
        // theme changed under it — is drawn before the box is looked at.
        if (writing)
            _notes.Restyle();

        // Nothing was drawn into the rendering while the markdown was on top of it, so coming back
        // to it is one of the ways it can be out of date.
        if (!writing)
            RenderNotes();

        if (focus && !_detail.Panel2Collapsed)
            (writing ? (Control)_notes : _rendered).Focus();
    }

    /// <summary>
    /// Opens the markdown to type into, with the caret where the reading was pointing.
    /// </summary>
    /// <param name="source">Where in the markdown to put the caret</param>
    private void StartWriting(int source)
    {
        // Nothing to type into. A completed task pulled out of the archive is held apart from the
        // live model, so an edit to it would be declined rather than queued.
        if (_notes.ReadOnly)
            return;

        SetNotesMode(writing: true, focus: true);

        // Straight through, with no arithmetic on the line endings: the rendering is drawn from
        // this box's own text, and a rich edit control holds a line ending as the single newline
        // the account stores it as — so an offset into the markdown is already an offset into the
        // box, and both agree with what gets saved.
        _notes.SelectionStart = Math.Clamp(source, 0, _notes.TextLength);
        _notes.SelectionLength = 0;
        _notes.ScrollToCaret();
    }

    /// <summary>Writes what was typed and goes back to reading.</summary>
    private void StopWriting()
    {
        SaveNotes();

        if (_writingNotes)
            SetNotesMode(writing: false, focus: false);
    }

    private void OnNotesKeyDown(object? sender, KeyEventArgs e)
    {
        // Back to reading, the way Escape leaves every other thing you are part-way through here.
        // The outline's own Escape does nothing, so this doesn't take a keystroke off anything.
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            StopWriting();
            _rendered.Focus();
            return;
        }

        // Ours rather than the control's, whose queue is switched off — see NotesHistory for why.
        // Ctrl+Z is bound to the task-level undo with the outline focused, and that is a different
        // scope from this one, so neither takes the other's keystroke.
        if (e.Control && e.KeyCode == Keys.Z && !e.Shift)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            PutNotesBack(_history.Undo(_notes.Text, _notes.SelectionStart));
            return;
        }

        if (e.Control && (e.KeyCode == Keys.Y || (e.KeyCode == Keys.Z && e.Shift)))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            PutNotesBack(_history.Redo());
        }
    }

    /// <summary>Puts an undone or redone state back into the box.</summary>
    /// <param name="state">What the box said, or null when there was nothing to go back to</param>
    private void PutNotesBack(NotesHistory.Snapshot? state)
    {
        if (state is not { } snapshot)
            return;

        _notes.TextChanged -= OnNotesChanged;
        try
        {
            _notes.Text = snapshot.Text;
            _notes.Restyle();

            // Where the edit was, not where the caret happened to be. Undoing to the top of a long
            // description and leaving the caret at the bottom of it reads as a broken undo even
            // when the text is right.
            _notes.SelectionStart = Math.Clamp(snapshot.Caret, 0, _notes.TextLength);
            _notes.SelectionLength = 0;
            _notes.ScrollToCaret();
        }
        finally
        {
            _notes.TextChanged += OnNotesChanged;
        }

        // An undo is an edit like any other as far as the account is concerned, and the handler
        // that would have said so was unhooked while this happened.
        _saveIdle.Stop();
        _saveIdle.Start();
    }

    /// <summary>Follows a link out of the notes.</summary>
    /// <remarks>
    /// Whether it is worth following was settled when it was drawn: a link the shell has no
    /// business being handed isn't coloured as one, isn't given a pointer, and never reaches here.
    /// The check inside <see cref="AppVersion.OpenExternal"/> is the one that decides, and this is
    /// downstream of it rather than a second opinion.
    /// </remarks>
    private void OnNotesLinkOpened(string url) => Guarded(() => AppVersion.OpenExternal(url));

    private void OnNotesChanged(object? sender, EventArgs e)
    {
        // Both restarted on each keystroke, so each happens once the typing pauses rather than
        // between two letters.
        _renderIdle.Stop();
        _renderIdle.Start();

        _saveIdle.Stop();
        _saveIdle.Start();
    }

    /// <summary>
    /// Writes what was typed, if anything was. Called whenever the box stops being the place the
    /// user is working: the focus leaving it, another task selected, the window closing.
    /// </summary>
    private void SaveNotes()
    {
        // Whatever brought us here has done what the wait was for.
        _saveIdle.Stop();

        // The draft puts the box's line endings back to the account's before it compares or saves.
        if (_draft.Take(_notes.Text) is not { } edit)
            return;

        Guarded(() => _presenter.SetDescription(edit.TaskId, edit.Text));
        _scheduler.NotifyWrite();
    }

    /// <summary>Opens or closes the notes panel.</summary>
    private void ShowDescriptionPanel(bool shown)
    {
        // Saved on the way out: the panel closing is the box losing the user as surely as the focus
        // leaving it, and a collapsed panel gives nothing back.
        if (!shown)
            SaveNotes();

        _adjustingPanels = true;
        try
        {
            _detail.Panel2Collapsed = !shown;
        }
        finally
        {
            _adjustingPanels = false;
        }

        if (!shown)
        {
            // Nothing left to draw on, so the wait for the typing to stop has nothing to wait for.
            _renderIdle.Stop();

            // Back to reading on the way out, so the panel opens the way it rests rather than in
            // whatever it was left mid-edit. Set through the same path as every other mode change,
            // since a flag moved on its own would disagree with which pane is actually on top.
            SetNotesMode(writing: false, focus: false);
            return;
        }

        ApplyPanelSizes();
        FollowSelection();
        RenderNotes();
    }

    /// <summary>Puts the panels back to the sizes the user left them at.</summary>
    private void ApplyPanelSizes()
    {
        _adjustingPanels = true;
        try
        {
            Restore();
        }
        finally
        {
            _adjustingPanels = false;
        }
    }

    private void Restore()
    {
        // Clamped against what the container can actually take, not against a figure of ours. The
        // sidebar splitter can be dragged until the outline is its own minimum width, and asking a
        // panel for more room than exists throws rather than settling for what there is.
        Restore(_detail, _detail.Height, _notesHeight, 60);

        static void Restore(SplitContainer split, int across, int wanted, int preferred)
        {
            if (split.Panel2Collapsed || across <= 0)
                return;

            var room = across - split.SplitterWidth;
            var least = Math.Max(preferred, split.Panel1MinSize);
            var most = room - Math.Max(preferred, split.Panel2MinSize);

            // No room for both halves at the sizes they each insist on. Left as it is rather than
            // set to something the control would refuse.
            if (most < least)
                return;

            split.SplitterDistance = Math.Clamp(room - wanted, least, most);
        }
    }

    /// <summary>Notes what the user has dragged the splitters to, for the next start.</summary>
    /// <remarks>
    /// Only a drag. SplitterMoved fires for every distance the layout settles on as well, and the
    /// window is laid out several times before it is on screen — each of those firing here wrote
    /// the container's unparented default over the size read out of the settings, so the saved
    /// height never survived to be applied and the collapsed figure was then saved back in its
    /// place.
    /// </remarks>
    private void RememberPanelSizes()
    {
        if (_adjustingPanels)
            return;

        if (!_detail.Panel2Collapsed)
            _notesHeight = _detail.Panel2.Height;
    }

    // ---- Commands ------------------------------------------------------------------------------

    /// <summary>Where a keystroke has to be pressed for it to mean what the table says.</summary>
    internal enum Scope
    {
        /// <summary>Anywhere in the window, whatever has the focus.</summary>
        Window,

        /// <summary>With the task outline focused.</summary>
        Outline,

        /// <summary>With the sidebar focused.</summary>
        Sidebar,
    }

    /// <summary>
    /// Every keystroke the app answers to, and what it asks for.
    /// </summary>
    /// <remarks>
    /// One table, read from both directions: the key handlers match against it, and the menus print
    /// from it — so a menu can't advertise a shortcut nothing is bound to. Where a command answers
    /// to two keystrokes the first is the one written down. Internal so a test can walk it.
    /// </remarks>
    internal static readonly (Keys Keys, AppCommand Command, Scope Scope)[] Shortcuts =
    [
        // On a row of the outline.
        (Keys.Space, AppCommand.ToggleComplete, Scope.Outline),
        (Keys.Control | Keys.Enter, AppCommand.ToggleComplete, Scope.Outline),
        (Keys.F2, AppCommand.Rename, Scope.Outline),
        (Keys.Control | Keys.D, AppCommand.Due, Scope.Outline),
        (Keys.Control | Keys.D1, AppCommand.Priority1, Scope.Outline),
        (Keys.Control | Keys.D2, AppCommand.Priority2, Scope.Outline),
        (Keys.Control | Keys.D3, AppCommand.Priority3, Scope.Outline),
        (Keys.Control | Keys.D4, AppCommand.Priority4, Scope.Outline),
        (Keys.Control | Keys.L, AppCommand.Labels, Scope.Outline),
        (Keys.Control | Keys.R, AppCommand.Reminders, Scope.Outline),
        (Keys.Tab, AppCommand.Indent, Scope.Outline),
        (Keys.Shift | Keys.Tab, AppCommand.Outdent, Scope.Outline),
        (Keys.Alt | Keys.Up, AppCommand.MoveUp, Scope.Outline),
        (Keys.Alt | Keys.Down, AppCommand.MoveDown, Scope.Outline),
        (Keys.Delete, AppCommand.Delete, Scope.Outline),

        // Kept off the window, where it would take Ctrl+Z away from every text box in it — undoing
        // a queued write instead of the word the user has just typed.
        (Keys.Control | Keys.Z, AppCommand.Undo, Scope.Outline),

        // On a row of the sidebar. F2 and Delete belong to the outline as well, which is what the
        // scope is for: the same key acts on whichever list the user is actually in.
        (Keys.F2, AppCommand.RenameSelection, Scope.Sidebar),
        (Keys.Delete, AppCommand.DeleteSelection, Scope.Sidebar),

        // Modified: a bare letter is TreeView's type-ahead, and favouriting is a write.
        (Keys.Control | Keys.Shift | Keys.F, AppCommand.ToggleFavourite, Scope.Sidebar),

        // Anywhere in the window.
        (Keys.Control | Keys.N, AppCommand.NewTask, Scope.Window),
        (Keys.Insert, AppCommand.NewTask, Scope.Window),
        (Keys.Control | Keys.Shift | Keys.N, AppCommand.NewProject, Scope.Window),
        (Keys.F5, AppCommand.SyncNow, Scope.Window),
        (Keys.Control | Keys.H, AppCommand.ToggleCompleted, Scope.Window),
        (Keys.Control | Keys.E, AppCommand.ToggleDescription, Scope.Window),
        (Keys.Control | Keys.F, AppCommand.Search, Scope.Window),
        (Keys.Control | Keys.K, AppCommand.Palette, Scope.Window),
        (Keys.Control | Keys.Up, AppCommand.PreviousView, Scope.Window),
        (Keys.Control | Keys.Down, AppCommand.NextView, Scope.Window),
        (Keys.Control | Keys.Oemcomma, AppCommand.Settings, Scope.Window),
    ];

    /// <summary>
    /// What a keystroke asks for where it was pressed, or <see cref="AppCommand.None"/> when it
    /// asks for nothing there. Internal so a test can check what each surface answers to without a
    /// window to type into.
    /// </summary>
    internal static AppCommand CommandFor(Keys keys, Scope scope)
        => Shortcuts.FirstOrDefault(s => s.Keys == keys && s.Scope == scope).Command;

    /// <summary>
    /// How a command's shortcut is written in a menu, or empty when it has none. Internal for the
    /// same reason: a menu that prints a shortcut nothing is bound to is the failure worth catching.
    /// </summary>
    internal static string ShortcutFor(AppCommand command)
    {
        var bound = Shortcuts.FirstOrDefault(s => s.Command == command);
        return bound.Command == AppCommand.None ? string.Empty : ShortcutText(bound.Keys);
    }

    /// <summary>
    /// A keystroke as a menu writes it — "Ctrl+1", "Shift+Tab", "Alt+↑". The framework's own
    /// converter is no use for the digits: it names them after the enum, so Ctrl+1 comes out
    /// "Ctrl+D1". Internal so a test can read what the menu will say.
    /// </summary>
    internal static string ShortcutText(Keys keys)
    {
        var parts = new List<string>(4);

        if (keys.HasFlag(Keys.Control))
            parts.Add("Ctrl");
        if (keys.HasFlag(Keys.Shift))
            parts.Add("Shift");
        if (keys.HasFlag(Keys.Alt))
            parts.Add("Alt");

        var code = keys & Keys.KeyCode;
        parts.Add(code switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
            Keys.Up => "↑",
            Keys.Down => "↓",
            Keys.Delete => "Del",
            Keys.Oemcomma => ",",
            // The enum's own name for this one is Return, which is not what the key says on it.
            Keys.Return => "Enter",
            _ => code.ToString(),
        });

        return string.Join("+", parts);
    }

    /// <summary>
    /// What the menus should be reading from right now — what is selected where, and what can be
    /// done to it. Gathered per open, so nothing has to be kept in step between times.
    /// </summary>
    private CommandContext Context() => new(
        _outline.SelectedRow,
        _presenter.AbilitiesFor(_outline.SelectedId),
        _sidebar.SelectedNode?.Tag as SidebarNode,
        _presenter.ShowingCompleted,
        _presenter.CanUndo,
        _presenter.Sort,
        !_detail.Panel2Collapsed,
        _writingNotes);

    /// <summary>
    /// How a command's shortcut is written here. Quick-add is the odd one out: its keystroke is the
    /// global hotkey, which the user picks and can switch off, so it is read from the settings
    /// rather than from the table.
    /// </summary>
    private string MenuShortcut(AppCommand command)
        => command != AppCommand.QuickAdd
            ? ShortcutFor(command)
            : _settings.HotkeyEnabled ? _settings.HotkeyBinding.ToString() : string.Empty;

    /// <summary>
    /// Renders menu entries into a strip, naming and greying each from <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Static, and given what to run and how to write a shortcut rather than reaching into the
    /// window for either, so a test can build a menu and click it with no window to hang it on.
    /// </remarks>
    /// <param name="items">The strip, or a submenu of one, to add to</param>
    /// <param name="entries">What to render, in order</param>
    /// <param name="context">What is selected, which decides the wording and what is greyed</param>
    /// <param name="shortcut">How to write a command's keystroke</param>
    /// <param name="run">Called with the command of whichever entry is clicked</param>
    internal static void FillMenu(
        ToolStripItemCollection items,
        IReadOnlyList<MenuEntry> entries,
        CommandContext context,
        Func<AppCommand, string> shortcut,
        Action<AppCommand> run)
    {
        foreach (var entry in entries)
        {
            if (entry.SeparatorBefore && items.Count > 0)
                items.Add(new ToolStripSeparator());

            if (entry.Children is { Count: > 0 } children)
            {
                var heading = new ToolStripMenuItem(entry.Heading);
                FillMenu(heading.DropDownItems, children, context, shortcut, run);

                // A submenu with nothing runnable in it is greyed, so Priority reads the same way
                // as everything beside it when no task is selected — rather than opening onto four
                // greyed priorities, which is a longer way of saying the same thing. The bar's own
                // headings never come through here: they stay open whatever is selected, because a
                // menu you can't look inside teaches nothing, and being able to look is the point.
                heading.Enabled = heading.DropDownItems.OfType<ToolStripMenuItem>().Any(i => i.Enabled);

                items.Add(heading);
                continue;
            }

            var state = Presentation.Commands.StateOf(entry.Command, context);
            var item = new ToolStripMenuItem(state.Label)
            {
                Enabled = state.Enabled,
                Checked = state.Checked,

                // Written on, not bound to. Setting ShortcutKeys would register the keystroke on
                // the strip as well as the control it belongs to, and which of the two answered
                // would stop being ours to say.
                ShortcutKeyDisplayString = shortcut(entry.Command),
            };

            item.Click += (_, _) => run(entry.Command);
            items.Add(item);
        }
    }

    /// <summary>Empties a strip and builds it again for what is selected now.</summary>
    private void Refill(ToolStripItemCollection items, IReadOnlyList<MenuEntry> entries)
    {
        // Disposed rather than dropped: the strip stops owning what it no longer holds, and a menu
        // rebuilt every time it opens would leak an item's worth of handles on each one.
        var stale = items.Cast<ToolStripItem>().ToList();
        items.Clear();
        foreach (var item in stale)
            item.Dispose();

        FillMenu(items, entries, Context(), MenuShortcut, Run);
    }

    /// <summary>
    /// Rebuilds the menu for the row it is about to open over. Built per open rather than once:
    /// what it says depends on the task — Complete or Reopen, which priority is ticked, whether
    /// there is anywhere left to move it — and a right-click is not a moment where fifteen menu
    /// items cost anything.
    /// </summary>
    private void OnTaskMenuOpening(object? sender, CancelEventArgs e)
    {
        // The outline turns away a right-click that missed every row, so what's left to catch here
        // is the keyboard asking for a menu with nothing selected — an empty list, most likely.
        if (_outline.SelectedRow is null)
        {
            e.Cancel = true;
            return;
        }

        Refill(_taskMenu.Items, Menus.TaskContext);
    }

    /// <summary>The menu bar, one top-level heading per group, each refilled as it opens.</summary>
    private MenuStrip BuildMenuBar()
    {
        var bar = new MenuStrip();

        foreach (var group in Menus.Bar)
        {
            var heading = new ToolStripMenuItem(group.Heading) { Tag = group };

            // Filled now as well as on opening: a heading with nothing under it has no dropdown to
            // open, so it would never get the chance to ask for one.
            Refill(heading.DropDownItems, group.Children ?? []);
            heading.DropDownOpening += OnMenuOpening;

            bar.Items.Add(heading);
        }

        return bar;
    }

    private void OnMenuOpening(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem { Tag: MenuEntry { Children: { } children } } heading)
            Refill(heading.DropDownItems, children);
    }

    /// <summary>Runs a command from wherever it was asked for, and tells the sync loop if it wrote.</summary>
    private void Run(AppCommand command)
    {
        Noticed();

        if (Dispatch(command))
            _scheduler.NotifyWrite();
    }

    /// <summary>
    /// Drops the cache notice, the user having demonstrably been at the keyboard.
    /// </summary>
    /// <remarks>
    /// Hung off input rather than off the selection, which moves on the first render as the rows
    /// arrive — that would clear it before the window had finished appearing. A keystroke, a click
    /// in either list, or a command from a menu are all things only a person does. Sitting and
    /// reading it leaves it up, which is the point.
    /// </remarks>
    private void Noticed()
    {
        if (_cacheNotice is null)
            return;

        _cacheNotice = null;
        RenderStatus();
    }

    /// <summary>
    /// Carries out a command, whichever surface asked for it — a keystroke, a menu, or the palette
    /// — so the three can't come to mean different things.
    /// </summary>
    /// <param name="command">The command to run</param>
    /// <returns>True when it changed something the server has yet to hear about</returns>
    private bool Dispatch(AppCommand command)
    {
        if (Presentation.Commands.IsTaskCommand(command))
            return _outline.SelectedId is { } id && RunTaskCommand(command, id);

        if (Presentation.Commands.IsSelectionCommand(command))
            return RunSelectionCommand(command);

        switch (command)
        {
            case AppCommand.NewTask:
                _capture.Focus();
                return false;

            case AppCommand.NewProject:
                return AddProject();

            case AppCommand.NewSection:
                return AddSection();

            case AppCommand.QuickAdd:
                Guarded(QuickAdd.Summon);
                return false;

            case AppCommand.SyncNow:
                _scheduler.RequestNow();
                return false;

            case AppCommand.ToggleCompleted:
                _ = ToggleCompletedAsync();
                return false;

            case AppCommand.SortDefault:
                Guarded(() => _presenter.ClearSort());
                return false;

            case AppCommand.ToggleDescription:
                Guarded(() => ShowDescriptionPanel(_detail.Panel2Collapsed));
                return false;

            case AppCommand.EditNotes:
                Guarded(() =>
                {
                    if (_writingNotes)
                        StopWriting();
                    else
                        StartWriting(_rendered.SourceAt(_rendered.SelectionStart));
                });
                return false;

            case AppCommand.Undo:
                var undone = false;
                Guarded(() =>
                {
                    undone = _presenter.Undo();
                    if (!undone)
                        _status.Text = "Nothing to undo.";
                });
                return undone;

            case AppCommand.Search:
                _search.Focus();
                _search.SelectAll();
                return false;

            case AppCommand.Palette:
                Guarded(OpenPalette);
                return false;

            case AppCommand.PreviousView:
                SwitchView(-1);
                return false;

            case AppCommand.NextView:
                SwitchView(1);
                return false;

            case AppCommand.Settings:
                Guarded(OpenSettings);
                return false;

            case AppCommand.CheckForUpdates:
                _ = CheckForUpdatesAsync();
                return false;

            case AppCommand.About:
                ShowAbout();
                return false;

            case AppCommand.Exit:
                Exit();
                return false;

            default:
                return false;
        }
    }

    /// <summary>Carries out a command on whichever sidebar row is selected.</summary>
    /// <returns>True when it wrote</returns>
    private bool RunSelectionCommand(AppCommand command)
    {
        if (_sidebar.SelectedNode?.Tag is not SidebarNode node)
            return false;

        return command switch
        {
            AppCommand.RenameSelection => RenameStructure(node),
            AppCommand.DeleteSelection => DeleteStructure(node),
            AppCommand.ToggleFavourite => ToggleFavourite(node),
            _ => false,
        };
    }

    /// <summary>Stars a project or a label, or takes the star off.</summary>
    /// <returns>True when it wrote — false over a row that has no star of its own</returns>
    private bool ToggleFavourite(SidebarNode node)
    {
        switch (node.Kind)
        {
            case SidebarKind.Project:
                Guarded(() => _presenter.ToggleProjectFavorite(node.Id));
                return true;

            case SidebarKind.Label:
                Guarded(() => _presenter.ToggleLabelFavorite(node.Id));
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Runs an action on a task, however it was asked for — a keystroke, a menu, or the palette.
    /// </summary>
    /// <param name="command">The action to run</param>
    /// <param name="id">The task to run it on</param>
    /// <returns>True when it changed something the server has yet to hear about</returns>
    private bool RunTaskCommand(AppCommand command, string id)
    {
        var wrote = false;

        switch (command)
        {
            // Ticking off a task that is already done means putting it back.
            case AppCommand.ToggleComplete:
                Guarded(() =>
                {
                    if (_outline.SelectedRow is { Completed: true })
                        _presenter.Reopen(id);
                    else
                        _presenter.Complete(id);
                });
                return true;

            // Nothing is written here: the editor commits when it closes, and that path syncs.
            case AppCommand.Rename:
                // Focused first, because this can come from a menu, and a menu has just taken the
                // focus off the outline — where the editor is about to open, on the row itself.
                _outline.Focus();
                BeginRename();
                return false;

            case AppCommand.Due:
                Guarded(() => wrote = PromptForDue(id));
                return wrote;

            case AppCommand.Labels:
                Guarded(() => wrote = PickLabels(id));
                return wrote;

            case AppCommand.Reminders:
                Guarded(() => wrote = ShowReminders(id));
                return wrote;

            case AppCommand.Priority1:
            case AppCommand.Priority2:
            case AppCommand.Priority3:
            case AppCommand.Priority4:
                Guarded(() => _presenter.SetPriority(id, Presentation.Commands.PriorityOf(command) ?? Priority.P4));
                return true;

            case AppCommand.Indent:
            case AppCommand.Outdent:
                Guarded(() =>
                {
                    var outdent = command == AppCommand.Outdent;
                    wrote = outdent ? _presenter.Outdent(id) : _presenter.Indent(id);

                    // Still said, though the menus now grey these out: the keyboard reaches them
                    // without a menu having opened to grey anything.
                    if (!wrote)
                        _status.Text = outdent ? "Already at the top level." : "Nothing above it to indent under.";
                });
                return wrote;

            case AppCommand.MoveUp:
            case AppCommand.MoveDown:
                Guarded(() =>
                {
                    wrote = _presenter.Move(id, command == AppCommand.MoveUp ? -1 : 1);
                    if (!wrote)
                        _status.Text = "Already at the end of its list.";
                });
                return wrote;

            case AppCommand.Delete:
                Guarded(() => _presenter.Delete(id));
                return true;

            default:
                return false;
        }
    }

    // ---- Outline keys --------------------------------------------------------------------------

    private void OnOutlineKeyDown(object? sender, KeyEventArgs e)
    {
        var command = CommandFor(e.KeyData, Scope.Outline);
        if (command == AppCommand.None)
            return;

        // A task command with no row under the cursor was never handled here, and letting the key
        // through is what keeps Tab moving the focus on out of an empty list.
        if (Presentation.Commands.IsTaskCommand(command) && _outline.SelectedId is null)
            return;

        // Left unhandled deliberately: the editor is open by the time the control sees its own F2,
        // and suppressing the key would stop the two agreeing about what is being edited.
        if (command == AppCommand.Rename)
        {
            BeginRename();
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        Run(command);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Every key in the window comes through here, which makes it the one place that sees the
        // user arrive however they arrive.
        Noticed();

        // Ahead of the table, because this runs before any control's own key handling and so the
        // sidebar can't claim Ctrl+N for itself: with a project under its cursor, Ctrl+N means a
        // section in that project rather than a task.
        if (keyData == (Keys.Control | Keys.N) && FocusedProject() is not null)
        {
            Run(AppCommand.NewSection);
            return true;
        }

        var command = CommandFor(keyData, Scope.Window);
        if (command == AppCommand.None)
            return base.ProcessCmdKey(ref msg, keyData);

        Run(command);
        return true;
    }

    /// <summary>Shows or hides completed tasks, fetching them the first time they're asked for.</summary>
    private async Task ToggleCompletedAsync()
    {
        // The fetch is a round trip over up to three months of history, so it is worth saying that
        // something is happening rather than leaving the list unchanged for a moment.
        if (!_presenter.ShowingCompleted)
            _status.Text = "Fetching completed tasks…";

        await GuardedAsync(async () =>
        {
            if (!await _presenter.ToggleCompletedAsync(_cts.Token))
                _status.Text = "Completed tasks need a connection.";
        });
    }

    private void OpenPalette()
    {
        if (CommandPaletteForm.Pick(this, _presenter.Palette, _theme) is not { } chosen)
            return;

        if (chosen.Selection is { } selection)
        {
            GoTo(selection);
            _outline.Focus();
            return;
        }

        // Through the same dispatch as a menu entry or a keystroke, so the palette can't be the
        // one surface where an action means something slightly different.
        Run(chosen.Command);
    }

    /// <summary>The project the sidebar is sitting on, when the sidebar is the one with focus.</summary>
    private SidebarNode? FocusedProject()
        => _sidebar.Focused && _sidebar.SelectedNode?.Tag is SidebarNode { Kind: SidebarKind.Project } node
            ? node
            : null;

    /// <summary>The project the sidebar is sitting on, wherever the focus happens to be.</summary>
    private SidebarNode? SelectedProject()
        => _sidebar.SelectedNode?.Tag is SidebarNode { Kind: SidebarKind.Project } node ? node : null;

    /// <returns>True when a project was created</returns>
    private bool AddProject()
    {
        var name = InputDialog.Ask(this, "New project", "Project name:");
        if (string.IsNullOrWhiteSpace(name))
            return false;

        Guarded(() => _presenter.AddProject(name));
        return true;
    }

    /// <summary>Adds a section to whichever project the sidebar is sitting on.</summary>
    /// <returns>True when a section was created</returns>
    private bool AddSection()
    {
        // Greyed in the menus, but the palette and Ctrl+N both reach this without one having been
        // opened, so the reason still has to be said out loud somewhere.
        if (SelectedProject() is not { } project)
        {
            _status.Text = "Pick a project first — a section belongs to one.";
            return false;
        }

        var name = InputDialog.Ask(this, "New section", "Section name:");
        if (string.IsNullOrWhiteSpace(name))
            return false;

        Guarded(() => _presenter.AddSection(name, project.Id));
        return true;
    }

    // ---- Inline rename -------------------------------------------------------------------------

    private void BeginRename()
    {
        if (_outline.SelectedIndices.Count > 0)
            _outline.Items[_outline.SelectedIndices[0]].BeginEdit();
    }

    private void OnBeforeLabelEdit(object? sender, LabelEditEventArgs e)
    {
        var row = _outline.SelectedRow;
        _editingId = row?.Id;
        _editingText = row?.Content;
    }

    private void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        // The list is rebuilt from the presenter before this returns, and the control then indexes
        // the edited row again — which throws if the rebuild dropped or moved it. Cancelling the
        // control's own commit on every path leaves the repaint to us.
        e.CancelEdit = true;

        var id = _editingId;
        var opened = _editingText;
        _editingId = null;
        _editingText = null;

        var text = e.Label;

        // Compare against what the editor was opened with, not the latest published content: a sync
        // may have renamed this task elsewhere while the box was open, and closing it unchanged
        // must not push that remote rename back.
        if (id is null || string.IsNullOrWhiteSpace(text) || text == opened)
        {
            OnRowsChanged(); // catch up on anything a sync published while the edit was open
            return;
        }

        Guarded(() => _presenter.Rename(id, text));
        _scheduler.NotifyWrite();
    }

    /// <summary>Asks for a due date and applies it. Returns false when nothing was changed.</summary>
    private bool PromptForDue(string id)
    {
        var answer = InputDialog.Ask(
            this,
            "Due date",
            "When is it due?  (today, friday, 2026-12-25, 4pm, every Monday — blank clears)");

        if (answer is null)
            return false;

        _presenter.SetDueFromText(id, answer);
        return true;
    }

    /// <summary>Shows the reminders on a task.</summary>
    /// <returns>True when one was added or removed.</returns>
    private bool ShowReminders(string id)
        => _outline.SelectedRow is { } row && ReminderForm.Show(this, _presenter, id, row.Content);

    /// <summary>Ticks the labels on a task, creating any the account doesn't have yet.</summary>
    private bool PickLabels(string id)
    {
        if (_outline.SelectedRow is not { } row)
            return false;

        var known = _presenter.Labels.Select(l => l.Name).ToList();
        var picked = LabelPickerForm.Pick(this, row.Content, known, row.Labels);
        if (picked is null)
            return false;

        // Opening the picker and pressing OK unchanged is not an edit. Writing anyway would queue a
        // command that owns this task until it lands, and a web-side edit arriving in the meantime
        // would be dropped for nothing.
        if (picked.Count == row.Labels.Count && !picked.Except(row.Labels, StringComparer.OrdinalIgnoreCase).Any())
            return false;

        // A name the user typed has to become a label first, so the item_update that follows refers
        // to something the account has. Names the task already wore are left alone: the picker
        // carries those so they aren't stripped, not so they're adopted into the account.
        var fresh = picked
            .Where(p => !known.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Where(p => !row.Labels.Contains(p, StringComparer.OrdinalIgnoreCase));

        foreach (var name in fresh)
            _presenter.AddLabel(name);

        _presenter.SetLabels(id, picked);
        return true;
    }

    // ---- Plumbing ------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        // Restored here rather than in the constructor: before the splitter is parented and sized it
        // clamps the distance against the container's default width and silently sticks there.
        _split.SplitterDistance = Math.Clamp(_settings.View.SidebarWidth, 120, Math.Max(120, ClientSize.Width - 200));
        ApplyPanelSizes();

        await GuardedAsync(() => _presenter.LoadAsync(_cts.Token));
        _scheduler.Start();
    }

    private void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private async Task<bool> GuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Report(ex);
            return false;
        }
    }

    private void Report(Exception ex, string prefix = "Something went wrong: ")
    {
        if (IsDisposed)
            return;

        if (ex is TodoistAuthException)
            _reconnectNeeded = true;

        // Once the token is gone every later sync fails the same way; keep the message that tells
        // the user what to do rather than replacing it with the consequence.
        if (_reconnectNeeded)
        {
            _status.Text = ReconnectMessage;
            return;
        }

        _status.Text = ex switch
        {
            OperationCanceledException => _status.Text, // window closed mid-flight
            _ => prefix + ex.Message,
        };
    }
}
