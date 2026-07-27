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
        _search.TextChanged += (_, _) => _presenter.Search(_search.Text);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            LabelEdit = true,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Task", 380);
        _list.Columns.Add("!", 44);
        _list.Columns.Add("Project", 150);
        _list.Columns.Add("Due", 140);
        _list.KeyDown += OnListKeyDown;
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
        Load += async (_, _) => await LoadAsync();
        FormClosing += (_, _) => _cts.Cancel();
        FormClosed += (_, _) =>
        {
            _presenter.RowsChanged -= OnRowsChanged;
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
        if (IsDisposed)
            return;

        var selectedId = SelectedId();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _presenter.Rows)
        {
            var item = new ListViewItem(new[] { row.Content, PriorityLabel(row.Priority), row.Project, row.Due })
            {
                Tag = row.Id,
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        if (selectedId is not null)
            Select(selectedId);

        _status.Text = _presenter.Status;
    }

    private static string PriorityLabel(Priority priority) => priority == Priority.P4 ? string.Empty : priority.ToString();

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
        await Guarded(() => _presenter.CaptureAsync(text, _cts.Token));
        _scheduler.NotifyWrite();
    }

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(_capture.Text))
        {
            _preview.Text = string.Empty;
            return;
        }

        var parse = _presenter.Preview(_capture.Text);
        var parts = new List<string> { $"\"{parse.Content}\"" };
        if (parse.ProjectName is not null)
            parts.Add("#" + parse.ProjectName);
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

    private async void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        var id = SelectedId();

        switch (e.KeyCode)
        {
            case Keys.Space when id is not null:
            case Keys.Enter when e.Control && id is not null:
                _presenter.Complete(id!);
                break;
            case Keys.Delete when id is not null:
                _presenter.Delete(id!);
                break;
            case Keys.F2 when id is not null:
                _list.SelectedItems[0].BeginEdit();
                return;
            case Keys.D when e.Control && id is not null:
                PromptForDue(id!);
                break;
            case Keys.Z when e.Control:
                _presenter.Undo();
                break;
            case Keys.D1 or Keys.D2 or Keys.D3 or Keys.D4 when e.Control && id is not null:
                _presenter.SetPriority(id!, (Priority)(e.KeyCode - Keys.D0));
                break;
            case Keys.Up when e.Alt && id is not null:
                _presenter.Move(id!, -1);
                break;
            case Keys.Down when e.Alt && id is not null:
                _presenter.Move(id!, 1);
                break;
            case Keys.F5:
                _scheduler.RequestNow();
                return;
            default:
                return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        _scheduler.NotifyWrite();
        await Task.CompletedTask;
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

    private void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        var text = e.Label;
        if (string.IsNullOrWhiteSpace(text))
        {
            e.CancelEdit = true;
            return;
        }

        if (_list.Items[e.Item].Tag is string id)
        {
            _presenter.Rename(id, text);
            _scheduler.NotifyWrite();
        }
    }

    private void PromptForDue(string id)
    {
        var answer = InputDialog.Ask(this, "Due date", "When is it due?  (today, tomorrow, friday, 2026-12-25, 4pm — blank clears)");
        if (answer is null)
            return;

        if (string.IsNullOrWhiteSpace(answer))
        {
            _presenter.SetDue(id, null);
            return;
        }

        var parse = _presenter.Preview(answer);
        if (parse.DueDate is null)
        {
            _status.Text = $"Couldn't read \"{answer}\" as a date.";
            return;
        }

        _presenter.SetDue(id, parse.DueDate, parse.DueTime);
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
                item.EnsureVisible();
                return;
            }
        }
    }

    private async Task LoadAsync()
    {
        await Guarded(() => _presenter.LoadAsync(_cts.Token));
        _scheduler.Start();
    }

    private async Task Guarded(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Window closed mid-flight.
        }
        catch (TodoistAuthException)
        {
            if (!IsDisposed)
                _status.Text = "Your Todoist token was rejected and cleared. Restart Termyn to reconnect.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _status.Text = "Something went wrong: " + ex.Message;
        }
    }
}
