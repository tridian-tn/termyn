using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// The writes the server refused, and the only way to be rid of them.
/// </summary>
/// <remarks>
/// A command the server keeps refusing stops being retried and stays in the outbox, which is what
/// keeps the count in the status bar honest. Without this that count was also all there was: a
/// permanent "1 failed" naming nothing, explaining nothing, and lasting until the cache was thrown
/// away.
///
/// Reading and dismissing only. Retrying is not offered, and deliberately: the engine put the local
/// copy back when it gave up, so sending the same command again would mean re-applying the change
/// first — and the commonest way to land here is a change the server will refuse every bit as
/// firmly the second time.
/// </remarks>
internal sealed class FailedChangesForm : Form
{
    private readonly ListBox _list;
    private readonly Label _reason;
    private readonly Button _dismiss;
    private readonly Func<IReadOnlyList<FailedChange>> _read;
    private readonly Action<string> _dismissed;

    private FailedChangesForm(Theme theme, Func<IReadOnlyList<FailedChange>> read, Action<string> dismissed)
    {
        _read = read;
        _dismissed = dismissed;

        Text = "Changes that didn't happen";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 360);
        BackColor = theme.Panel;
        ForeColor = theme.Text;

        var heading = new Label
        {
            // Not "these are not in your account", which is only true of the ones the server
            // refused. The ones it never answered about may well have landed, and each says so.
            Text = "Termyn couldn't complete these changes.",
            Location = new Point(14, 12),
            Size = new Size(492, 20),
            ForeColor = theme.Muted,
        };

        _list = new ListBox
        {
            Location = new Point(14, 38),
            Size = new Size(492, 180),
            IntegralHeight = false,
            BackColor = theme.Panel,
            ForeColor = theme.Text,
        };
        _list.SelectedIndexChanged += (_, _) => ShowReason();

        _reason = new Label
        {
            Location = new Point(14, 228),
            Size = new Size(492, 76),
            ForeColor = theme.Muted,
        };

        _dismiss = new Button { Text = "Dismiss", Location = new Point(14, 316), Size = new Size(110, 30) };
        _dismiss.Click += (_, _) => Dismiss();

        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Location = new Point(418, 316), Size = new Size(88, 30) };

        AcceptButton = close;
        CancelButton = close;
        Controls.AddRange([heading, _list, _reason, _dismiss, close]);

        Fill();
    }

    /// <summary>What is currently picked, or null when the list is empty.</summary>
    private FailedChange? Selected => _list.SelectedItem as FailedChange;

    /// <summary>Puts the failures in the list, keeping the place where there still is one.</summary>
    private void Fill()
    {
        var at = _list.SelectedIndex;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var failure in _read())
            _list.Items.Add(failure);
        _list.EndUpdate();

        if (_list.Items.Count == 0)
        {
            // Nothing left to read, so there is nothing for this window to be about.
            Close();
            return;
        }

        _list.SelectedIndex = Math.Clamp(at, 0, _list.Items.Count - 1);
        ShowReason();
    }

    /// <summary>What the server said about the picked one, and what letting it go would cost.</summary>
    private void ShowReason()
    {
        _dismiss.Enabled = Selected is not null;

        if (Selected is not { } failure)
        {
            _reason.Text = string.Empty;
            return;
        }

        var said = failure.Reason is { } reason
            ? $"Todoist said: {reason}"
            : "Todoist gave no reason.";

        // Where it ended up, which only a refusal actually settles. Saying "it never reached your
        // account" of a change the server simply went quiet about would be a guess, and the wrong
        // guess would have someone delete their only copy of something the account already has.
        var landed = failure.Unruled
            ? "Todoist never reported a result, so whether your account has this is unknown."
            : "Todoist refused it, so your account doesn't have it.";

        // Said before it happens rather than asked about afterwards. Dismissing a change that was
        // already put back costs nothing, and this is the one time it costs something.
        var cost = failure.DiscardsWork
            ? "Dismissing removes Termyn's copy, which is the only one on this machine."
            : "Termyn has already put this back. Dismissing only clears the notice.";

        _reason.Text = $"{said}\r\n{landed}\r\n\r\n{cost}";
    }

    private void Dismiss()
    {
        if (Selected is not { } failure)
            return;

        _dismissed(failure.Uuid);
        Fill();
    }

    /// <summary>
    /// Shows the failures, letting them be dismissed as they are read.
    /// </summary>
    /// <param name="owner">The window to sit over</param>
    /// <param name="theme">The colours to draw with</param>
    /// <param name="read">The failures as they now stand, asked for again after each dismissal</param>
    /// <param name="dismissed">Called with the one to let go</param>
    public static void Show(
        IWin32Window owner,
        Theme theme,
        Func<IReadOnlyList<FailedChange>> read,
        Action<string> dismissed)
    {
        if (read().Count == 0)
            return;

        using var dialog = new FailedChangesForm(theme, read, dismissed);
        dialog.ShowDialog(owner);
    }
}
