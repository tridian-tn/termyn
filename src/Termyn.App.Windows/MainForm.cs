using Termyn.Core.Api;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>Main window: a read-only list of active tasks, rendered from cache then reconciled.</summary>
internal sealed class MainForm : Form
{
    private readonly MainPresenter _presenter;
    private readonly ListView _list;
    private readonly Label _status;
    private readonly CancellationTokenSource _cts = new();

    public MainForm(MainPresenter presenter)
    {
        _presenter = presenter;

        Text = "Termyn";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 480);
        MinimumSize = new Size(480, 320);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("!", 40);
        _list.Columns.Add("Task", 380);
        _list.Columns.Add("Project", 160);
        _list.Columns.Add("Due", 120);

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

        _presenter.RowsChanged += OnRowsChanged;
        Load += async (_, _) => await LoadAsync();
        FormClosing += (_, _) => _cts.Cancel();
        FormClosed += (_, _) =>
        {
            _presenter.RowsChanged -= OnRowsChanged;
            _cts.Dispose();
        };
    }

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

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _presenter.Rows)
        {
            _list.Items.Add(new ListViewItem(new[]
            {
                row.Priority.ToString(),
                row.Content,
                row.Project,
                row.Due,
            }));
        }
        _list.EndUpdate();

        _status.Text = _presenter.Status;
    }

    private async Task LoadAsync()
    {
        try
        {
            await _presenter.LoadAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Window closed while the first sync was still running.
        }
        catch (TodoistAuthException)
        {
            if (!IsDisposed)
                _status.Text = "Your Todoist token was rejected and cleared. Restart Termyn to reconnect.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                _status.Text = "Failed to load: " + ex.Message;
        }
    }
}
