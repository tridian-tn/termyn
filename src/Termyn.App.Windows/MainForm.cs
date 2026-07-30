using System.Diagnostics;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
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
    bool StartInTray = false,
    bool StartWithQuickAdd = false);

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

    /// <summary>Held from startup, when there is no status bar yet to put it in.</summary>
    private string? _hotkeyNotice;

    public MainForm(MainPresenter presenter, SyncScheduler scheduler, Shell shell)
    {
        _presenter = presenter;
        _scheduler = scheduler;
        _shell = shell;
        _settings = shell.Settings;
        _theme = Theme.Resolve(_settings.Theme);

        Text = "Termyn";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(940, 580);
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
        _sidebar.AfterSelect += OnSidebarSelect;
        _sidebar.KeyDown += OnSidebarKeyDown;

        _outline = new OutlineView { Dock = DockStyle.Fill };
        _outline.KeyDown += OnOutlineKeyDown;
        _outline.BeforeLabelEdit += OnBeforeLabelEdit;
        _outline.AfterLabelEdit += OnAfterLabelEdit;

        _unsupported = new LinkLabel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 4, 8, 4),
            Visible = false,
        };
        _unsupported.LinkClicked += (_, _) => OpenTodoist();

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
        };
        _split.Panel1.Controls.Add(_sidebar);
        _split.Panel2.Controls.Add(_outline);

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
        _shell.Instance.SignalReceived += OnInstanceSignal;

        BuildTrayMenu();
        _hotkeyNotice = RegisterHotkey(announce: false);

        Load += async (_, _) => await LoadAsync();
        Shown += OnShown;
        FormClosing += OnFormClosing;
        FormClosed += (_, _) =>
        {
            _presenter.RowsChanged -= OnRowsChanged;
            _presenter.StatusChanged -= OnStatusChanged;
            _scheduler.SyncFailed -= OnSyncFailed;
            _shell.Hotkey.Pressed -= OnHotkey;
            _shell.Notifier.Activated -= OnTrayActivated;
            _shell.Instance.SignalReceived -= OnInstanceSignal;

            _quickAdd?.AllowClose();
            _quickAdd?.Dispose();
            _headerFont?.Dispose();
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

        if (hotkeyChanged && RegisterHotkey(announce: true) is { } hotkeyNotice)
            notices.Add(hotkeyNotice);

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

        // Said once, on the first paint: at registration time there was no status bar to put it in.
        if (_hotkeyNotice is { } notice)
        {
            _hotkeyNotice = null;
            _status.Text = notice;
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
        _outline.Rows = _presenter.Rows;
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
        Guarded(() => Process.Start(new ProcessStartInfo("https://app.todoist.com/app/filters") { UseShellExecute = true }));
    }

    private void RenderSidebar()
    {
        // Nothing structural changed — a search keystroke, say — so leave the tree alone rather
        // than rebuilding it and losing what the user has collapsed.
        if (ReferenceEquals(_renderedSidebar, _presenter.Sidebar))
            return;

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
    }

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
        Guarded(() => _presenter.Select(MainPresenter.SelectionOf(node)));
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
        if (_sidebar.SelectedNode?.Tag is not SidebarNode node)
            return;

        switch (e.KeyCode)
        {
            case Keys.F2 when node.Kind is SidebarKind.Project or SidebarKind.Section or SidebarKind.Label:
                RenameStructure(node);
                break;
            case Keys.Delete when node.Kind is SidebarKind.Project or SidebarKind.Section or SidebarKind.Label:
                DeleteStructure(node);
                break;
            // Modified: a bare letter is TreeView's type-ahead, and favouriting is a write.
            case Keys.F when e.Control && e.Shift && node.Kind == SidebarKind.Project:
                Guarded(() => _presenter.ToggleProjectFavorite(node.Id));
                break;
            case Keys.F when e.Control && e.Shift && node.Kind == SidebarKind.Label:
                Guarded(() => _presenter.ToggleLabelFavorite(node.Id));
                break;
            default:
                return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        _scheduler.NotifyWrite();
    }

    private void RenameStructure(SidebarNode node)
    {
        var name = InputDialog.Ask(this, "Rename", "New name:", node.Label);
        if (string.IsNullOrWhiteSpace(name))
            return;

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
    }

    private void DeleteStructure(SidebarNode node)
    {
        // A label delete takes the label off its tasks; the other two take the tasks with them.
        var question = node.Kind == SidebarKind.Label
            ? $"Delete the label \"{node.Label}\" and remove it from every task?"
            : $"Delete the {(node.Kind == SidebarKind.Project ? "project" : "section")} \"{node.Label}\" and everything in it?";

        var answer = MessageBox.Show(this, question, "Termyn", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK)
            return;

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

    // ---- Outline keys --------------------------------------------------------------------------

    private void OnOutlineKeyDown(object? sender, KeyEventArgs e)
    {
        var id = _outline.SelectedId;
        var wrote = true;

        switch (e.KeyCode)
        {
            // Ticking off a task that is already done means putting it back.
            case Keys.Space when id is not null:
            case Keys.Enter when e.Control && id is not null:
                Guarded(() =>
                {
                    if (_outline.SelectedRow is { Completed: true })
                        _presenter.Reopen(id!);
                    else
                        _presenter.Complete(id!);
                });
                break;
            case Keys.Delete when id is not null:
                Guarded(() => _presenter.Delete(id!));
                break;
            case Keys.F2 when id is not null:
                BeginRename();
                return;
            case Keys.Tab when id is not null:
                wrote = false;
                Guarded(() =>
                {
                    wrote = e.Shift ? _presenter.Outdent(id!) : _presenter.Indent(id!);
                    if (!wrote)
                        _status.Text = e.Shift ? "Already at the top level." : "Nothing above it to indent under.";
                });
                break;
            case Keys.D when e.Control && id is not null:
                wrote = false;
                Guarded(() => wrote = PromptForDue(id!));
                break;
            case Keys.L when e.Control && id is not null:
                wrote = false;
                Guarded(() => wrote = PickLabels(id!));
                break;
            case Keys.R when e.Control && id is not null:
                wrote = false;
                Guarded(() => wrote = ShowReminders(id!));
                break;
            case Keys.Z when e.Control:
                wrote = false;
                Guarded(() =>
                {
                    wrote = _presenter.Undo();
                    if (!wrote)
                        _status.Text = "Nothing to undo.";
                });
                break;
            case Keys.D1 or Keys.D2 or Keys.D3 or Keys.D4 when e.Control && id is not null:
                Guarded(() => _presenter.SetPriority(id!, (Priority)(e.KeyCode - Keys.D0)));
                break;
            case Keys.Up when e.Alt && id is not null:
            case Keys.Down when e.Alt && id is not null:
                wrote = false;
                Guarded(() =>
                {
                    wrote = _presenter.Move(id!, e.KeyCode == Keys.Up ? -1 : 1);
                    if (!wrote)
                        _status.Text = "Already at the end of its list.";
                });
                break;
            case Keys.F5:
                _scheduler.RequestNow();
                return;
            default:
                return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        if (wrote)
            _scheduler.NotifyWrite();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.F:
                _search.Focus();
                _search.SelectAll();
                return true;
            case Keys.Control | Keys.K:
                Guarded(OpenPalette);
                return true;
            case Keys.Control | Keys.H:
                _ = ToggleCompletedAsync();
                return true;
            case Keys.Control | Keys.Oemcomma:
                Guarded(OpenSettings);
                return true;
            case Keys.Control | Keys.Up:
                SwitchView(-1);
                return true;
            case Keys.Control | Keys.Down:
                SwitchView(1);
                return true;
            // This runs ahead of every control's own key handling, so the sidebar can't claim
            // Ctrl+N for itself — the choice between a section and a task has to be made here.
            case Keys.Control | Keys.N when FocusedProject() is { } project:
                AddSection(project.Id);
                return true;
            case Keys.Insert:
            case Keys.Control | Keys.N:
                _capture.Focus();
                return true;
            case Keys.Control | Keys.Shift | Keys.N:
                AddProject();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
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

        switch (chosen.Command)
        {
            case PaletteCommand.NewTask:
                _capture.Focus();
                break;
            case PaletteCommand.NewProject:
                AddProject();
                break;
            // Whatever the sidebar is sitting on, since the palette is what has the focus here.
            case PaletteCommand.NewSection when SelectedProject() is { } project:
                AddSection(project.Id);
                break;
            case PaletteCommand.NewSection:
                _status.Text = "Pick a project first — a section belongs to one.";
                break;
            case PaletteCommand.SyncNow:
                _scheduler.RequestNow();
                break;
            case PaletteCommand.ToggleCompleted:
                _ = ToggleCompletedAsync();
                break;
            case PaletteCommand.Undo:
                Guarded(() =>
                {
                    if (!_presenter.Undo())
                        _status.Text = "Nothing to undo.";
                    else
                        _scheduler.NotifyWrite();
                });
                break;
            case PaletteCommand.Settings:
                OpenSettings();
                break;
        }
    }

    /// <summary>The project the sidebar is sitting on, when the sidebar is the one with focus.</summary>
    private SidebarNode? FocusedProject()
        => _sidebar.Focused && _sidebar.SelectedNode?.Tag is SidebarNode { Kind: SidebarKind.Project } node
            ? node
            : null;

    /// <summary>The project the sidebar is sitting on, wherever the focus happens to be.</summary>
    private SidebarNode? SelectedProject()
        => _sidebar.SelectedNode?.Tag is SidebarNode { Kind: SidebarKind.Project } node ? node : null;

    private void AddProject()
    {
        var name = InputDialog.Ask(this, "New project", "Project name:");
        if (string.IsNullOrWhiteSpace(name))
            return;

        Guarded(() => _presenter.AddProject(name));
        _scheduler.NotifyWrite();
    }

    private void AddSection(string projectId)
    {
        var name = InputDialog.Ask(this, "New section", "Section name:");
        if (string.IsNullOrWhiteSpace(name))
            return;

        Guarded(() => _presenter.AddSection(name, projectId));
        _scheduler.NotifyWrite();
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
