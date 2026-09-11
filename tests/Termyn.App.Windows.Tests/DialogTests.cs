using Termyn.Core.Settings;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The dialogs as they lay themselves out, checked without ever putting one on screen.
/// </summary>
public class DialogTests
{
    private static Theme AnyTheme => Theme.Resolve(ThemePreference.Light);

    private static SettingsForm NewSettings()
    {
        var form = new SettingsForm(new AppSettings(), AnyTheme);

        // Laid out on creation rather than on being shown, so what is measured below is what the
        // user would see.
        form.CreateControl();
        return form;
    }

    /// <summary>Every control in a window, and everything nested inside them.</summary>
    private static IEnumerable<Control> Every(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Every(child))
                yield return nested;
        }
    }

    // ---- The one-line prompt -------------------------------------------------------------------

    [WinFormsTheory]
    [InlineData("Buy milk", "Under: Buy milk")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void The_sub_task_prompt_names_its_parent_when_it_has_one(string? parent, string? expected)
    {
        // Blank as well as absent: a task with no content to show would give a line reading "Under:"
        // and nothing after it, which says less than saying nothing.
        Assert.Equal(expected, InputDialog.Under(parent));
    }

    [WinFormsFact]
    public void A_prompt_can_name_what_is_being_added_to()
    {
        // A sub-task is added from the palette and from a keystroke as well as from a menu opened
        // over the row, so which task is about to gain a child isn't always the one last looked at.
        using var dialog = InputDialog.Subtask("Buy milk");
        dialog.CreateControl();

        Assert.Contains(Every(dialog).OfType<Label>(), l => l.Text == "Under: Buy milk");
    }

    [WinFormsFact]
    public void A_prompt_with_nothing_to_name_says_nothing()
    {
        // Every other caller has one thing it could be acting on and has already said so in the
        // title. An empty line there would be a gap the eye stops at.
        using var plain = new InputDialog("Rename", "New name:", "Work");
        using var named = InputDialog.Subtask("Buy milk");
        plain.CreateControl();
        named.CreateControl();

        Assert.Single(Every(plain).OfType<Label>());
        Assert.True(
            named.ClientSize.Height > plain.ClientSize.Height,
            "the named one should be taller by the line it carries");
    }

    [WinFormsFact]
    public void The_box_is_still_the_thing_to_type_in()
    {
        // The context line sits above the box, not in it. Typed over, it would be the sub-task's
        // own text — which is how a "New sub-task" called "Under: Buy milk" would get created.
        using var dialog = InputDialog.Subtask("Buy milk");
        dialog.CreateControl();

        var box = Assert.Single(Every(dialog).OfType<TextBox>());
        Assert.Equal(string.Empty, box.Text);

        // And below the line rather than behind it.
        var line = Every(dialog).OfType<Label>().Single(l => l.Text.StartsWith("Under:", StringComparison.Ordinal));
        Assert.True(box.Top >= line.Bottom, "the box should sit below the line naming the parent");
    }

    // ---- The command palette -------------------------------------------------------------------

    [WinFormsFact]
    public void The_command_palette_opens_with_the_caret_in_the_search_box()
    {
        // It opens to be typed at. With the focus on the results list instead, the first letters
        // went to that list's own type-ahead and never reached the query.
        using var palette = new CommandPaletteForm(_ => [], AnyTheme);

        Assert.IsType<TextBox>(palette.ActiveControl);
    }

    // ---- The settings dialog -------------------------------------------------------------------

    [WinFormsFact]
    public void No_setting_has_its_words_cut_off()
    {
        // Worth having, but it can only ever measure against the font this machine happens to run
        // — and the truncation it is named for showed up under a wider one than that. The test
        // below is the one that actually holds the layout to sizing itself.
        using var form = NewSettings();

        var clipped = Every(form)
            .Where(c => c is CheckBox or Button or Label)
            .Where(c => c.PreferredSize.Width > c.Width)
            .Select(c => $"{c.Text} needs {c.PreferredSize.Width} and has {c.Width}")
            .ToList();

        Assert.Empty(clipped);
    }

    [WinFormsFact]
    public void Every_modifier_is_named_in_full()
    {
        using var form = NewSettings();

        var boxes = Every(form).OfType<CheckBox>().Select(c => c.Text).ToList();

        Assert.Contains("Ctrl", boxes);
        Assert.Contains("Alt", boxes);
        Assert.Contains("Shift", boxes);
        Assert.Contains("Win", boxes);
    }

    [WinFormsFact]
    public void The_modifiers_size_themselves_to_their_own_text()
    {
        // Which is what stops the truncation coming back the next time the font changes.
        using var form = NewSettings();

        var modifiers = Every(form)
            .OfType<CheckBox>()
            .Where(c => c.Text is "Ctrl" or "Alt" or "Shift" or "Win")
            .ToList();

        Assert.Equal(4, modifiers.Count);
        Assert.All(modifiers, c => Assert.True(c.AutoSize, $"{c.Text} is sized by hand"));
    }

    [WinFormsFact]
    public void Nothing_hangs_off_the_edge_of_the_dialog()
    {
        using var form = NewSettings();

        var outside = Every(form)
            .Where(c => c.Parent == form)
            .Where(c => c.Right > form.ClientSize.Width || c.Bottom > form.ClientSize.Height)
            .Select(c => $"{c.GetType().Name} '{c.Text}' at {c.Bounds}")
            .ToList();

        Assert.Empty(outside);
    }

    [WinFormsFact]
    public void Nothing_in_the_dialog_sits_on_top_of_anything_else()
    {
        using var form = NewSettings();

        var laid = Every(form).Where(c => c.Parent == form).ToList();

        var overlapping =
            from a in laid
            from b in laid
            where !ReferenceEquals(a, b) && a.Bounds.IntersectsWith(b.Bounds)
            select $"'{a.Text}' {a.Bounds} over '{b.Text}' {b.Bounds}";

        Assert.Empty(overlapping.ToList());
    }

    [WinFormsFact]
    public void The_dialog_leaves_room_for_the_longest_thing_it_says()
    {
        // The line that appears when the chosen combination won't register is the widest text in
        // the window, and it is the one with least room to spare.
        using var form = NewSettings();

        var warning = Every(form).OfType<Label>().Single(l => l.Text.Length == 0);
        var longest = TextRenderer.MeasureText(
            $"Needs Ctrl, Alt or Win — otherwise {HotkeyBinding.Default} is used.",
            form.Font);

        Assert.True(warning.Width >= longest.Width, $"the warning has {warning.Width} and needs {longest.Width}");
    }
}
