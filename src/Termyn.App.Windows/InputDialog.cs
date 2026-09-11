namespace Termyn.App.Windows;

/// <summary>A one-line prompt, used for the small text inputs the keyboard map needs.</summary>
internal sealed class InputDialog : Form
{
    /// <summary>How tall the dialog is without a context line, and how much one adds.</summary>
    private const int BaseHeight = 132;

    private const int ContextHeight = 24;

    private readonly TextBox _input;

    /// <summary>Internal rather than private so a test can lay one out without showing it.</summary>
    internal InputDialog(string title, string prompt, string initial = "", string? context = null)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;

        var extra = context is null ? 0 : ContextHeight;
        ClientSize = new Size(420, BaseHeight + extra);

        var label = new Label { Text = prompt, Location = new Point(14, 14), Size = new Size(392, 20) };
        var controls = new List<Control> { label };

        if (context is not null)
        {
            // Above the box rather than in the title bar: a task's content runs to a sentence and a
            // title bar would cut it without saying it had. Greyed and ellipsised, since it is what
            // is being added to rather than anything to be typed over — and a long one still has to
            // leave the box it belongs to looking like the thing to fill in.
            controls.Add(new Label
            {
                Text = context,
                Location = new Point(14, 36),
                Size = new Size(392, 20),
                ForeColor = SystemColors.GrayText,
                AutoEllipsis = true,
            });
        }

        _input = new TextBox { Text = initial, Location = new Point(14, 40 + extra), Size = new Size(392, 27) };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(226, 84 + extra), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(318, 84 + extra), Size = new Size(88, 30) };

        AcceptButton = ok;
        CancelButton = cancel;
        controls.AddRange([_input, ok, cancel]);
        Controls.AddRange([.. controls]);
        Shown += (_, _) => _input.SelectAll();
    }

    /// <summary>
    /// Asks for a new sub-task, naming the task it will hang under.
    /// </summary>
    /// <remarks>
    /// Its own entry point rather than a call to <see cref="Ask"/> with one more argument: the
    /// naming is the point of this prompt, and an argument is a thing that can quietly go missing.
    /// </remarks>
    /// <param name="owner">The window to centre on</param>
    /// <param name="parent">What the parent task is called, or null when it isn't in the view</param>
    /// <returns>The sub-task's text, or null when the dialog was cancelled</returns>
    public static string? AskForSubtask(IWin32Window owner, string? parent)
    {
        using var dialog = Subtask(parent);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._input.Text : null;
    }

    /// <summary>
    /// The sub-task prompt as it will be shown, built and not shown.
    /// </summary>
    /// <remarks>
    /// Internal so a test can look at the dialog this actually puts up. Asserting on the wording
    /// alone left the one thing worth checking — that the wording reaches the window — resting on
    /// nobody having deleted an argument.
    /// </remarks>
    /// <param name="parent">What the parent task is called, or null when it isn't in the view</param>
    /// <returns>The dialog, which the caller disposes</returns>
    internal static InputDialog Subtask(string? parent)
        => new("New sub-task", "Sub-task:", context: Under(parent));

    /// <summary>
    /// How the prompt names the task a sub-task is going under.
    /// </summary>
    /// <param name="parent">What the parent is called, or null when it isn't known</param>
    /// <returns>The line to show, or null when there is nothing to name</returns>
    internal static string? Under(string? parent)
        => string.IsNullOrWhiteSpace(parent) ? null : $"Under: {parent}";

    /// <summary>
    /// Shows the prompt and returns the entered text, or <c>null</c> if cancelled.
    /// </summary>
    /// <param name="owner">The window to centre on</param>
    /// <param name="title">The dialog's own title</param>
    /// <param name="prompt">What to ask for, above the box</param>
    /// <param name="initial">What the box starts with, selected ready to be typed over</param>
    /// <param name="context">What is being acted on, shown between the prompt and the box</param>
    /// <returns>The text entered, or null when the dialog was cancelled</returns>
    public static string? Ask(
        IWin32Window owner,
        string title,
        string prompt,
        string initial = "",
        string? context = null)
    {
        using var dialog = new InputDialog(title, prompt, initial, context);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._input.Text : null;
    }
}
