namespace Termyn.App.Windows;

/// <summary>A one-line prompt, used for the small text inputs the keyboard map needs.</summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _input;

    private InputDialog(string title, string prompt, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 132);

        var label = new Label { Text = prompt, Location = new Point(14, 14), Size = new Size(392, 20) };
        _input = new TextBox { Text = initial, Location = new Point(14, 40), Size = new Size(392, 27) };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(226, 84), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(318, 84), Size = new Size(88, 30) };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([label, _input, ok, cancel]);
        Shown += (_, _) => _input.SelectAll();
    }

    /// <summary>Shows the prompt and returns the entered text, or <c>null</c> if cancelled.</summary>
    public static string? Ask(IWin32Window owner, string title, string prompt, string initial = "")
    {
        using var dialog = new InputDialog(title, prompt, initial);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._input.Text : null;
    }
}
