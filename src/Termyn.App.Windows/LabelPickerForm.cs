using Termyn.Presentation;

using Label = System.Windows.Forms.Label;

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
        Text = "Labels";
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

        var prompt = new Label { Text = "New label:", Location = new Point(14, 298), Size = new Size(80, 20) };
        _fresh = new TextBox { Location = new Point(94, 295), Size = new Size(160, 27), PlaceholderText = "name" };

        var add = new Button { Text = "Add", Location = new Point(260, 294), Size = new Size(86, 29) };
        add.Click += (_, _) => AddFresh();

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(166, 352), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(258, 352), Size = new Size(88, 30) };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([heading, _list, prompt, _fresh, add, ok, cancel]);

        // Enter in the name box adds a label rather than accepting the dialog, which would discard it.
        _fresh.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.Handled = true;
            e.SuppressKeyPress = true;
            AddFresh();
        };
    }

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
