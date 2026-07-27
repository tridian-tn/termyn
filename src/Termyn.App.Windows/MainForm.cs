using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>Main window: capture box, task list, and the keyboard map.</summary>
internal sealed class MainForm : Form
{
    private readonly MainPresenter _presenter;
    private readonly SyncScheduler _scheduler;
    private readonly CancellationTokenSource _cts = new();

    private readonly TextBox _capture;
    private readonly Label _preview;
    private readonly TextBox _search;
    private readonly ListView _list;
    private readonly Label _status;

    private string? _editingId;

    public MainForm(MainPresenter presenter, SyncScheduler scheduler)
    {
        _presenter = presenter;
        _scheduler = scheduler;

        Text = "Termyn";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(520, 360);
        KeyPreview = true;

        _capture = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Add a task…  #project @label p1 tomorrow 4pm" };
        _capture.KeyDown += OnCaptureKeyDown;
        _capture.TextChanged += (_, _) => UpdatePreview();

        _preview = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = SystemColors.GrayText, Padding = new Padding(4, 2, 0, 0) };

        _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search…" };
        _search.TextChanged += (_, _) => Guarded(() => _presenter.Search(_search.Text));

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            LabelEdit = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Task", 380);
        _list.Columns.Add("!", 44);
        _list.Columns.Add("Project", 150);
        _list.Columns.Add("Due", 140);
        _list.KeyDown += OnListKeyDown;
        _list.BeforeLabelEdit += OnBeforeLabelEdit;
        _list.AfterLabelEdit += OnAfterLabelEdit;

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Text = "Loading…",
        };

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(_search);
        Controls.Add(_preview);
        Controls.Add(_capture);

        _presenter.RowsChanged += OnRowsChanged;
        _scheduler.SyncFailed += OnSyncFailed;
        Load += async (_, _) => await LoadAsync();
        FormClosing += (_, _) => _cts.Cancel();
        FormClosed += (_, _) =>
        {
            _presenter.RowsChanged -= OnRowsChanged;
            _scheduler.SyncFailed -= OnSyncFailed;
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

        var selectedId = SelectedId();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _presenter.Rows)
        {
            _list.Items.Add(new ListViewItem(new[] { row.Content, PriorityLabel(row.Priority), row.Project, row.Due })
            {
                Tag = row.Id,
            });
        }
        _list.EndUpdate();

        if (selectedId is not null)
            Select(selectedId);

        _status.Text = _presenter.Status;
    }

    private static string PriorityLabel(Priority priority) => priority == Priority.P4 ? string.Empty : priority.ToString();

    private void OnSyncFailed(Exception ex)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            _status.Text = ex is TodoistAuthException
                ? "Your Todoist token was rejected and cleared. Restart Termyn to reconnect."
                : "Background sync failed: " + ex.Message;
        });
    }

    // ---- Input ---------------------------------------------------------------------------------

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
        await GuardedAsync(() => _presenter.CaptureAsync(text, _cts.Token));
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

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        var id = SelectedId();
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
                _list.SelectedItems[0].BeginEdit();
                return;
            case Keys.D when e.Control && id is not null:
                // A cancelled or unreadable date changes nothing, so it shouldn't trigger a sync.
                wrote = false;
                Guarded(() => wrote = PromptForDue(id!));
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
                Guarded(() => _presenter.Move(id!, -1));
                break;
            case Keys.Down when e.Alt && id is not null:
                Guarded(() => _presenter.Move(id!, 1));
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
            case Keys.Insert:
            case Keys.Control | Keys.N:
                _capture.Focus();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnBeforeLabelEdit(object? sender, LabelEditEventArgs e)
        => _editingId = e.Item >= 0 && e.Item < _list.Items.Count ? _list.Items[e.Item].Tag as string : null;

    private void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        // Capture the id up front: a sync landing mid-edit could otherwise shift what this index means.
        var id = _editingId;
        _editingId = null;

        var text = e.Label;
        if (id is null || string.IsNullOrWhiteSpace(text))
        {
            e.CancelEdit = true;
            OnRowsChanged(); // catch up on anything a sync published while the edit was open
            return;
        }

        var current = _presenter.Rows.FirstOrDefault(r => r.Id == id)?.Content;
        if (text == current)
            return;

        Guarded(() => _presenter.Rename(id, text));
        _scheduler.NotifyWrite();
    }

    /// <summary>Asks for a due date and applies it. Returns false when nothing was changed.</summary>
    private bool PromptForDue(string id)
    {
        var answer = InputDialog.Ask(this, "Due date", "When is it due?  (today, tomorrow, friday, 2026-12-25, 4pm — blank clears)");
        if (answer is null)
            return false;

        if (string.IsNullOrWhiteSpace(answer))
        {
            _presenter.SetDue(id, null);
            return true;
        }

        var parse = _presenter.Preview(answer).Parse;
        if (parse.DueDate is null)
        {
            _status.Text = $"Couldn't read \"{answer}\" as a date.";
            return false;
        }

        _presenter.SetDue(id, parse.DueDate, parse.DueTime);
        return true;
    }

    // ---- Plumbing ------------------------------------------------------------------------------

    private string? SelectedId()
        => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    private void Select(string id)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if ((item.Tag as string) == id)
            {
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
                return;
            }
        }
    }

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

    private async Task GuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void Report(Exception ex)
    {
        if (IsDisposed)
            return;

        _status.Text = ex switch
        {
            OperationCanceledException => _status.Text, // window closed mid-flight
            TodoistAuthException => "Your Todoist token was rejected and cleared. Restart Termyn to reconnect.",
            _ => "Something went wrong: " + ex.Message,
        };
    }
}
