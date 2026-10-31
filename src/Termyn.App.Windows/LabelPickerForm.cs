namespace Termyn.App.Windows;

/// <summary>
/// Ticks the labels on a task. Labels are joined by name, so this deals in names throughout and
/// hands back the whole set — Todoist has no way to add or remove just one.
/// </summary>
internal sealed class LabelPickerForm : Form
{
    private readonly CheckedListBox _list;
    private readonly TextBox _fresh;

    private LabelPickerForm(string task, IReadOnlyList<string> known, IReadOnlyList<string> applied)
    {
        Text = Strings.LabelsTitle;
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 400);

        var heading = new Label
        {
            Text = task,
            Location = new Point(14, 12),
            Size = new Size(332, 20),
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,

            // The task's own name, in which an ampersand is a character rather than an access key.
            UseMnemonic = false,
        };

        _list = new CheckedListBox
        {
            Location = new Point(14, 38),
            Size = new Size(332, 250),
            CheckOnClick = true,
            IntegralHeight = false,
        };

        foreach (var name in known)
            _list.Items.Add(name, applied.Contains(name, StringComparer.OrdinalIgnoreCase));

        // A label the task wears that the account doesn't list — one left behind by a rename, say.
        // Dropping it silently would take it off the task the moment this dialog is accepted.
        foreach (var orphan in applied.Where(a => !known.Contains(a, StringComparer.OrdinalIgnoreCase)))
            _list.Items.Add(orphan, true);

        var prompt = new Label { Text = Strings.LabelsNew, Location = new Point(14, 298), Size = new Size(80, 20) };
        _fresh = new TextBox { Location = new Point(94, 295), Size = new Size(160, 27), PlaceholderText = Strings.LabelsNamePlaceholder };

        var add = new Button { Text = Strings.LabelsAdd, Location = new Point(260, 294), Size = new Size(86, 29) };
        add.Click += (_, _) => AddFresh();

        var ok = new Button { Text = Strings.ButtonOK, DialogResult = DialogResult.OK, Location = new Point(166, 352), Size = new Size(88, 30) };
        var cancel = new Button { Text = Strings.ButtonCancel, DialogResult = DialogResult.Cancel, Location = new Point(258, 352), Size = new Size(88, 30) };

        // A name still sitting in the box is one the user meant to apply. Click runs before the
        // dialog closes, so taking it here is the difference between adding the label and losing it.
        ok.Click += (_, _) => AddFresh();

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([heading, _list, prompt, _fresh, add, ok, cancel]);
    }

    /// <summary>
    /// Enter in the name box adds the label and leaves the dialog open for another.
    /// </summary>
    /// <remarks>
    /// Here rather than on the box's KeyDown, which never saw the key: a single-line box hands Enter
    /// on, and the accept button took it first and closed the dialog. An empty box still accepts.
    /// </remarks>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Enter && _fresh.Focused && !string.IsNullOrWhiteSpace(_fresh.Text))
        {
            AddFresh();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    /// <summary>The dialog, built and not shown, for a test to drive.</summary>
    internal static LabelPickerForm For(string task, IReadOnlyList<string> known, IReadOnlyList<string> applied)
        => new(task, known, applied);

    /// <summary>Types a name and presses Enter in its box, for a test with no keyboard.</summary>
    /// <param name="name">The name, as it would be typed</param>
    /// <returns>Whether the dialog took the key</returns>
    internal bool EnterName(string name)
    {
        _fresh.Focus();
        _fresh.Text = name;
        return ProcessDialogKey(Keys.Enter);
    }

    /// <summary>The labels ticked so far.</summary>
    internal IReadOnlyList<string> Ticked => _list.CheckedItems.Cast<object>().Select(o => o.ToString()!).ToList();

    /// <summary>The labels that ended up ticked, or <c>null</c> if the dialog was cancelled.</summary>
    public static IReadOnlyList<string>? Pick(
        IWin32Window owner,
        string task,
        IReadOnlyList<string> known,
        IReadOnlyList<string> applied)
    {
        using var dialog = new LabelPickerForm(task, known, applied);
        if (dialog.ShowDialog(owner) != DialogResult.OK)
            return null;

        return dialog._list.CheckedItems.Cast<object>().Select(o => o.ToString()!).ToList();
    }

    private void AddFresh()
    {
        var name = _fresh.Text.Trim();
        if (name.Length == 0)
            return;

        // Already listed: tick it rather than adding a second row for the same label.
        for (var i = 0; i < _list.Items.Count; i++)
        {
            if (string.Equals(_list.Items[i].ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                _list.SetItemChecked(i, true);
                _fresh.Clear();
                return;
            }
        }

        _list.SetItemChecked(_list.Items.Add(name), true);
        _fresh.Clear();
    }
}
