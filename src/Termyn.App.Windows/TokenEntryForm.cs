using System.Diagnostics;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>
/// Token dialog: paste a Todoist API token and validate it. On first run it's stored on success;
/// on a reconnect it's handed back, for the window to check whose it is first.
/// </summary>
internal sealed class TokenEntryForm : Form
{
    private const string IntegrationsUrl = "https://app.todoist.com/app/settings/integrations/developer";

    private readonly AuthPresenter _auth;
    private readonly bool _reconnecting;
    private readonly TextBox _tokenBox;
    private readonly Label _status;
    private readonly Button _connect;

    /// <param name="auth">What checks the token</param>
    /// <param name="reconnecting">
    /// True once Todoist has turned the last token away: the new one is handed back through
    /// <see cref="Accepted"/> rather than stored
    /// </param>
    public TokenEntryForm(AuthPresenter auth, bool reconnecting = false)
    {
        _auth = auth;
        _reconnecting = reconnecting;

        Text = reconnecting ? "Reconnect Termyn to Todoist" : "Connect Termyn to Todoist";
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(468, 214);

        var intro = new Label
        {
            Text = reconnecting
                ? "Todoist turned the last token away. Paste the current one (Settings → Integrations → Developer → API token)."
                : "Paste your Todoist API token (Settings → Integrations → Developer → API token).",
            Location = new Point(16, 16),
            Size = new Size(436, 40),
        };

        var link = new LinkLabel
        {
            Text = "Open Todoist integration settings",
            AutoSize = true,
            Location = new Point(16, 58),
        };
        link.LinkClicked += (_, _) => OpenUrl(IntegrationsUrl);

        _tokenBox = new TextBox
        {
            Location = new Point(16, 88),
            Size = new Size(436, 27),
            UseSystemPasswordChar = true,
        };

        _status = new Label
        {
            ForeColor = Color.Firebrick,
            Location = new Point(16, 122),
            Size = new Size(436, 24),
        };

        _connect = new Button
        {
            Text = "Connect",
            Location = new Point(352, 164),
            Size = new Size(100, 32),
            DialogResult = DialogResult.None,
        };
        _connect.Click += async (_, _) => await OnConnectAsync();

        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(244, 164),
            Size = new Size(100, 32),
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = _connect;
        CancelButton = cancel;
        Controls.AddRange([intro, link, _tokenBox, _status, _connect, cancel]);
    }

    private async Task OnConnectAsync()
    {
        _status.Text = string.Empty;
        _connect.Enabled = false;
        _tokenBox.Enabled = false;
        try
        {
            var result = _reconnecting
                ? await HandBackAsync()
                : await _auth.ValidateAndStoreAsync(_tokenBox.Text);

            switch (result)
            {
                case TokenValidationResult.Valid:
                    DialogResult = DialogResult.OK;
                    break;
                case TokenValidationResult.Rejected:
                    _status.Text = "That token was rejected. Check it and try again.";
                    break;
                case TokenValidationResult.NetworkError:
                    _status.Text = "Couldn't reach Todoist (it may be offline or busy). Check your connection and retry.";
                    break;
            }
        }
        catch (Exception)
        {
            _status.Text = "Something went wrong saving the token. Please try again.";
        }
        finally
        {
            _connect.Enabled = true;
            _tokenBox.Enabled = true;
        }
    }

    /// <summary>
    /// The token Todoist accepted on a reconnect, with the account it belongs to. Null until then,
    /// and always on a first run, where the token is stored instead.
    /// </summary>
    public TokenCheck? Accepted { get; private set; }

    private async Task<TokenValidationResult> HandBackAsync()
    {
        var check = await _auth.ValidateAsync(_tokenBox.Text);
        if (check.Result is TokenValidationResult.Valid)
            Accepted = check;
        return check.Result;
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            _status.Text = "Couldn't open the browser. Copy this link manually: " + url;
        }
    }
}
