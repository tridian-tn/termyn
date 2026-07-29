using System.Diagnostics;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

// Both namespaces have a Label, and in a form file the control is what "Label" should mean.
using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>Main window: sidebar, capture box, task outline, and the keyboard map.</summary>
internal sealed class MainForm : Form
{
    private const string ReconnectMessage = "Your Todoist token was rejected and cleared. Restart Termyn to reconnect.";

    private readonly MainPresenter _presenter;
    private readonly SyncScheduler _scheduler;
    private readonly CancellationTokenSource _cts = new();

    private readonly TextBox _capture;
    private readonly Label _preview;
    private readonly TextBox _search;
    private readonly TreeView _sidebar;
    private readonly OutlineView _outline;
    private readonly Label _status;

    /// <summary>Shown in place of a result when the selected filter is beyond the local grammar.</summary>
    private readonly LinkLabel _unsupported;

    private string? _editingId;
    private string? _editingText;
    private bool _reconnectNeeded;
    private bool _syncingSidebar;

    /// <summary>The sidebar row the user actually clicked, which the id alone can't identify.</summary>
    private string _sidebarKey = ViewSelection.Default.Key;

    /// <summary>The sidebar last rendered, so an unchanged one isn't rebuilt.</summary>
    private IReadOnlyList<SidebarNode>? _renderedSidebar;

    private Font? _headerFont;

    public MainForm(MainPresenter presenter, SyncScheduler scheduler)
    {
        _presenter = presenter;
        _scheduler = scheduler;

        Text = "Termyn";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(940, 580);
        MinimumSize = new Size(640, 400);
        KeyPreview = true;

        _capture = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Add a task…  #project /section @label p1 tomorrow 4pm" };
        _capture.KeyDown += OnCaptureKeyDown;
        _capture.TextChanged += (_, _) => UpdatePreview();

        _preview = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = SystemColors.GrayText, Padding = new Padding(4, 2, 0, 0) };

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

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
        };
        split.Panel1.Controls.Add(_sidebar);
        split.Panel2.Controls.Add(_outline);

        // Above the outline, so it reads as an explanation of the empty list below it.
        split.Panel2.Controls.Add(_unsupported);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Text = "Loading…",
        };

        Controls.Add(split);
        Controls.Add(_status);
        Controls.Add(_search);
        Controls.Add(_preview);
        Controls.Add(_capture);

        // Only once it's parented: before that the splitter is clamped against the container's
        // default 150px width and silently sticks there.
        split.SplitterDistance = 220;

        _headerFont = new Font(_sidebar.Font, FontStyle.Bold);

        _presenter.RowsChanged += OnRowsChanged;
        _scheduler.SyncFailed += OnSyncFailed;
        Load += async (_, _) => await LoadAsync();
        FormClosing += (_, _) => _cts.Cancel();
        FormClosed += (_, _) =>
        {
            _presenter.RowsChanged -= OnRowsChanged;
            _scheduler.SyncFailed -= OnSyncFailed;
            _headerFont?.Dispose();
            _cts.Dispose();
        };
    }

    // ---- Rendering -----------------------------------------------------------------------------

    private void OnRowsChanged()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(Render);
            return;
        }
        Render();
    }

    private void Render()
    {
        // A background sync must not wipe the row the user is currently renaming.
        if (IsDisposed || _editingId is not null)
            return;

        RenderSidebar();
        _outline.Rows = _presenter.Rows;
        _status.Text = _presenter.Status;
        RenderUnsupported();
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
            // Remember which branches are closed; the rebuild would otherwise reopen them.
            var collapsed = CollapsedKeys();

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
                    tree.ForeColor = SystemColors.GrayText;
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
        var collapsed = new HashSet<string>();
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


    private void OnSyncFailed(Exception ex)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;

            if (ex is TodoistAuthException)
                _reconnectNeeded = true;

            _status.Text = _reconnectNeeded ? ReconnectMessage : "Background sync failed: " + ex.Message;
        });
    }

    // ---- Navigation ----------------------------------------------------------------------------

    private void OnSidebarSelect(object? sender, TreeViewEventArgs e)
    {
        if (_syncingSidebar || e.Node?.Tag is not SidebarNode node)
            return;

        // A group label isn't a view; leave the outline where it was.
        if (node.Kind == SidebarKind.Header)
            return;

        _sidebarKey = node.Key;
        Guarded(() => _presenter.Select(node.Kind switch
        {
            SidebarKind.SmartView => ViewSelection.Of(node.View ?? SmartView.Today),
            SidebarKind.Section => ViewSelection.OfSection(node.Id),
            SidebarKind.Label => ViewSelection.OfLabel(node.Id),
            SidebarKind.Filter => ViewSelection.OfFilter(node.Id),
            _ => ViewSelection.OfProject(node.Id),
        }));
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

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(_capture.Text))
        {
            _preview.Text = string.Empty;
            return;
        }

        var preview = _presenter.Preview(_capture.Text);
        var parse = preview.Parse;
        var parts = new List<string> { $"\"{parse.Content}\"" };

        if (parse.ProjectName is { } project)
            parts.Add(preview.ProjectResolved ? "#" + project : $"#{project} (unknown — goes to Inbox)");
        if (parse.SectionName is { } section)
            parts.Add(preview.SectionResolved ? "/" + section : $"/{section} (unknown)");
        foreach (var label in parse.Labels)
            parts.Add("@" + label);
        if (parse.Priority != Priority.P4)
            parts.Add(parse.Priority.ToString());
        if (parse.DueDate is { } date)
            parts.Add(parse.DueTime is { } time ? $"{date:yyyy-MM-dd} {time:HH:mm}" : $"{date:yyyy-MM-dd}");
        if (parse.Unsupported.Count > 0)
            parts.Add("(needs a connection: " + string.Join(", ", parse.Unsupported) + ")");

        _preview.Text = string.Join("  ·  ", parts);
    }

    // ---- Outline keys --------------------------------------------------------------------------

    private void OnOutlineKeyDown(object? sender, KeyEventArgs e)
    {
        var id = _outline.SelectedId;
        var wrote = true;

        switch (e.KeyCode)
        {
            case Keys.Space when id is not null:
            case Keys.Enter when e.Control && id is not null:
                Guarded(() => _presenter.Complete(id!));
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

    /// <summary>The project the sidebar is sitting on, when the sidebar is the one with focus.</summary>
    private SidebarNode? FocusedProject()
        => _sidebar.Focused && _sidebar.SelectedNode?.Tag is SidebarNode { Kind: SidebarKind.Project } node
            ? node
            : null;

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

    private void Report(Exception ex)
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
            _ => "Something went wrong: " + ex.Message,
        };
    }
}
