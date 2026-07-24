using System.Diagnostics;
using Termyn.Presentation;

namespace Termyn.App.Windows;

/// <summary>First-run dialog: paste a Todoist API token, validate it, and store it on success.</summary>
internal sealed class TokenEntryForm : Form
{
    private const string IntegrationsUrl = "https://app.todoist.com/app/settings/integrations/developer";

    private readonly AuthPresenter _auth;
    private readonly TextBox _tokenBox;
    private readonly Label _status;
    private readonly Button _connect;

    public TokenEntryForm(AuthPresenter auth)
    {
        _auth = auth;

        Text = "Connect Termyn to Todoist";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(468, 214);

        var intro = new Label
        {
            Text = "Paste your Todoist API token (Settings → Integrations → Developer → API token).",
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
            switch (await _auth.ValidateAndStoreAsync(_tokenBox.Text))
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
