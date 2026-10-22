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

    /// <summary>What the box was summoned holding, so Enter with nothing added to it adds nothing.</summary>
    private string? _start;

    public QuickAddForm(MainPresenter presenter, Theme theme)
    {
        _presenter = presenter;

        Text = Strings.QuickAddTitle;
        Icon = AppIcon.Window;
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
            Hint = CapturePreviewText.Hint(presenter.DatesInEnglish),
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
    public void Summon() => Summon(null);

    /// <summary>
    /// Brings the box up on the monitor the user is working on, focused, and holding whatever the
    /// task starts with.
    /// </summary>
    /// <remarks>
    /// The pointer's screen rather than the active window's: the hotkey is most often pressed while
    /// working in another application, and the pointer is where the user is looking.
    ///
    /// The hint is set again each time, since the box is built at start-up and on a first run that's
    /// before the account has said which language it reads dates in.
    ///
    /// A start goes after the caret, with a space before it, so what's typed comes first and reads
    /// the way a task is written: "Buy milk #Errands" rather than "#Errands Buy milk". Todoist reads
    /// the words in any order.
    /// </remarks>
    /// <param name="start">What the task begins with, or null to begin with nothing</param>
    public void Summon(string? start)
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            screen.X + ((screen.Width - Width) / 2),
            screen.Y + (screen.Height / 4));

        _capture.Hint = CapturePreviewText.Hint(_presenter.DatesInEnglish);
        Fill(start);

        Show();

        // A window that was already up but behind something else has to be pulled forward; Show
        // alone won't do it, and TopMost alone doesn't take the focus.
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        _capture.Focus();

        // After the focus, which can select the whole box on its way in.
        _capture.Select(0, 0);
    }

    /// <summary>
    /// Puts what the task starts with in the box, with the caret ahead of it.
    /// </summary>
    /// <remarks>
    /// Internal so a test can see what the box would hold without putting it on screen, on top of
    /// whatever else is running.
    /// </remarks>
    /// <param name="start">What the task begins with, or null to begin with nothing</param>
    internal void Fill(string? start)
    {
        _start = string.IsNullOrWhiteSpace(start) ? null : " " + start.Trim();
        _capture.Text = _start ?? string.Empty;
        _capture.Select(0, 0);
        UpdatePreview();
    }

    /// <summary>What's in the box.</summary>
    internal string Typed => _capture.Text;

    /// <summary>Where the caret is in it.</summary>
    internal int Caret => _capture.SelectionStart;

    /// <summary>
    /// Whether Enter on some text adds a task.
    /// </summary>
    /// <remarks>
    /// Not for nothing, and not for what the box was summoned holding with nothing added to it. A
    /// project or a day alone isn't a task: sent as one, the server would make a task of the words
    /// themselves, and the box would have added something nobody typed.
    /// </remarks>
    /// <param name="text">What's in the box</param>
    /// <returns>True when there's a task in it</returns>
    internal bool Adds(string text) => !string.IsNullOrWhiteSpace(text) && text.Trim() != _start?.Trim();

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

        await Add(_capture.Text);
    }

    /// <summary>
    /// Adds what's in the box as a task, or just closes it when there's no task in it.
    /// </summary>
    /// <remarks>
    /// Internal so a test can do what Enter does without a key to press. What New task put in the
    /// box goes along with it, so a capture that falls back to the local grammar can still read the
    /// day it named.
    /// </remarks>
    /// <param name="text">What's in the box</param>
    /// <returns>Completes once the capture is made, or the box is back up after a failure</returns>
    internal async Task Add(string text)
    {
        if (!Adds(text))
        {
            Hide();
            return;
        }

        // Hidden first: capture goes to the network when it can, and the box lingering while that
        // happens is the difference between quick-add feeling instant and feeling like a dialog.
        Hide();

        var start = _start?.Trim();

        try
        {
            await _presenter.CaptureAsync(text, start);
            Captured?.Invoke();
        }
        catch (Exception ex)
        {
            // Nothing was created anywhere, so the typing comes back rather than being lost, and
            // with it whatever New task had started it with.
            Failed?.Invoke(ex);
            Summon(start);
            _capture.Text = text;
            _capture.SelectionStart = text.Length;
        }
    }

    private void UpdatePreview() => _preview.Text = _presenter.PreviewText(_capture.Text);
}
