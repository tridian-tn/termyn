using Termyn.Presentation;

// Both namespaces have a Label, and in a form file the control is what it should mean.
using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// The global quick-add box: a single line that captures a task and gets out of the way.
/// </summary>
/// <remarks>
/// Created once at startup and hidden rather than closed, because the hotkey has 100 ms to put it on
/// screen and building a form is most of that. Closing it hides it; only shutdown disposes it.
/// </remarks>
internal sealed class QuickAddForm : Form
{
    private readonly MainPresenter _presenter;
    private readonly HintTextBox _capture;
    private readonly Label _preview;

    private bool _shuttingDown;

    public QuickAddForm(MainPresenter presenter, Theme theme)
    {
        _presenter = presenter;

        Text = "Termyn — quick add";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        ClientSize = new Size(560, 76);

        // Drawn rather than set as PlaceholderText: this box is summoned already focused, and
        // WinForms shows a placeholder only while a box isn't.
        _capture = new HintTextBox
        {
            Dock = DockStyle.Top,
            Height = 30,
            Hint = CapturePreviewText.Hint,
        };
        _capture.KeyDown += OnKeyDown;
        _capture.TextChanged += (_, _) => UpdatePreview();

        _preview = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(4, 6, 4, 0),
            AutoEllipsis = true,
        };

        Controls.Add(_preview);
        Controls.Add(_capture);

        ApplyTheme(theme);

        // Hidden, not destroyed: the next hotkey press has to find it already built.
        FormClosing += (_, e) =>
        {
            if (_shuttingDown)
                return;
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>Raised after a task has been captured, so the caller can nudge the sync loop.</summary>
    public event Action? Captured;

    /// <summary>Raised when something went wrong, since this window has nowhere to report it.</summary>
    public event Action<Exception>? Failed;

    public void ApplyTheme(Theme theme)
    {
        theme.Apply(this);
        _preview.ForeColor = theme.Muted;
        _capture.HintColour = theme.Muted;
    }

    /// <summary>
    /// Brings the box up on the monitor the user is working on, empty and focused.
    /// </summary>
    /// <remarks>
    /// The pointer's screen rather than the active window's: the hotkey is most often pressed while
    /// working in another application, and the pointer is where the user is looking.
    /// </remarks>
    public void Summon()
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            screen.X + ((screen.Width - Width) / 2),
            screen.Y + (screen.Height / 4));

        _capture.Clear();
        UpdatePreview();

        Show();

        // A window that was already up but behind something else has to be pulled forward; Show
        // alone won't do it, and TopMost alone doesn't take the focus.
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        _capture.Focus();
    }

    /// <summary>Lets the box actually close when the application is shutting down.</summary>
    public void AllowClose() => _shuttingDown = true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
            return;

        e.SuppressKeyPress = true;

        var text = _capture.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            Hide();
            return;
        }

        // Hidden first: capture goes to the network when it can, and the box lingering while that
        // happens is the difference between quick-add feeling instant and feeling like a dialog.
        Hide();

        try
        {
            await _presenter.CaptureAsync(text);
            Captured?.Invoke();
        }
        catch (Exception ex)
        {
            // Nothing was created anywhere, so the typing comes back rather than being lost.
            Failed?.Invoke(ex);
            Summon();
            _capture.Text = text;
            _capture.SelectionStart = text.Length;
        }
    }

    private void UpdatePreview() => _preview.Text = _presenter.PreviewText(_capture.Text);
}
