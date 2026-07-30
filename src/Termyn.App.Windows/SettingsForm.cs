using Termyn.Core.Settings;

// Both namespaces have a Label, and in a form file the control is what it should mean.
using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// The settings dialog: hotkey, theme, sync cadence, and what Termyn does at login and on close.
/// </summary>
/// <remarks>
/// Laid out in code like the rest of the windows here, so there is no designer file to keep in step
/// and no generated partial to read past.
/// </remarks>
internal sealed class SettingsForm : Form
{
    private readonly CheckBox _hotkeyEnabled;
    private readonly CheckBox _ctrl;
    private readonly CheckBox _alt;
    private readonly CheckBox _shift;
    private readonly CheckBox _win;
    private readonly ComboBox _key;
    private readonly ComboBox _theme;
    private readonly ComboBox _syncMode;
    private readonly NumericUpDown _interval;
    private readonly CheckBox _launchAtLogin;
    private readonly CheckBox _closeToTray;
    private readonly Label _warning;

    private SettingsForm(AppSettings settings, Theme theme, bool autoStartAvailable)
    {
        Text = "Termyn — settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 344);

        var binding = settings.HotkeyBinding;

        _hotkeyEnabled = Check("Global quick-add hotkey", settings.HotkeyEnabled, 14);
        _ctrl = Check("Ctrl", binding.Modifiers.HasFlag(HotkeyModifiers.Control), 40, 24, 58);
        _alt = Check("Alt", binding.Modifiers.HasFlag(HotkeyModifiers.Alt), 40, 86, 52);
        _shift = Check("Shift", binding.Modifiers.HasFlag(HotkeyModifiers.Shift), 40, 142, 60);
        _win = Check("Win", binding.Modifiers.HasFlag(HotkeyModifiers.Meta), 40, 206, 52);

        _key = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(268, 36),
            Size = new Size(90, 26),
        };
        foreach (var name in HotkeyBinding.AllowedKeys)
            _key.Items.Add(name);
        _key.SelectedItem = binding.Key;
        if (_key.SelectedIndex < 0)
            _key.SelectedIndex = 0;

        _warning = new Label
        {
            Location = new Point(14, 66),
            Size = new Size(410, 20),
            ForeColor = theme.Accent,
        };

        _theme = Choice(typeof(ThemePreference), settings.Theme, 118);
        _syncMode = Choice(typeof(SyncMode), settings.SyncMode, 158);

        _interval = new NumericUpDown
        {
            Location = new Point(160, 196),
            Size = new Size(90, 26),
            Minimum = AppSettings.MinSyncIntervalSeconds,
            Maximum = AppSettings.MaxSyncIntervalSeconds,
            Value = settings.ClampedInterval,
        };

        _launchAtLogin = Check("Start Termyn when I sign in", settings.LaunchAtLogin, 238, width: 300);
        _launchAtLogin.Enabled = autoStartAvailable;

        _closeToTray = Check("Closing the window leaves Termyn in the tray", settings.CloseToTray, 266, width: 400);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(244, 302), Size = new Size(88, 30) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(338, 302), Size = new Size(88, 30) };

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange(
        [
            _hotkeyEnabled, _ctrl, _alt, _shift, _win, _key, _warning,
            Caption("Theme", 122), _theme,
            Caption("Background sync", 162), _syncMode,
            Caption("Every (seconds)", 200), _interval,
            _launchAtLogin, _closeToTray, ok, cancel,
        ]);

        if (!autoStartAvailable)
        {
            // Better said than left as a box that silently does nothing when ticked.
            _launchAtLogin.Text += "  (unavailable — Termyn can't find its own binary)";
        }

        _hotkeyEnabled.CheckedChanged += (_, _) => Sync();
        _syncMode.SelectedIndexChanged += (_, _) => Sync();
        foreach (var box in new[] { _ctrl, _alt, _win })
            box.CheckedChanged += (_, _) => Sync();

        theme.Apply(this);
        _warning.ForeColor = theme.Accent;
        Sync();
    }

    /// <summary>Shows the dialog and returns the amended settings, or null if cancelled.</summary>
    public static AppSettings? Edit(IWin32Window owner, AppSettings settings, Theme theme, bool autoStartAvailable)
    {
        using var form = new SettingsForm(settings, theme, autoStartAvailable);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Apply(settings) : null;
    }

    private AppSettings Apply(AppSettings settings) => settings with
    {
        Hotkey = Binding().ToString(),
        HotkeyEnabled = _hotkeyEnabled.Checked,
        Theme = (ThemePreference)_theme.SelectedItem!,
        SyncMode = (SyncMode)_syncMode.SelectedItem!,
        SyncIntervalSeconds = (int)_interval.Value,
        LaunchAtLogin = _launchAtLogin.Checked,
        CloseToTray = _closeToTray.Checked,
    };

    private HotkeyBinding Binding()
    {
        var modifiers = HotkeyModifiers.None;
        if (_ctrl.Checked) modifiers |= HotkeyModifiers.Control;
        if (_alt.Checked) modifiers |= HotkeyModifiers.Alt;
        if (_shift.Checked) modifiers |= HotkeyModifiers.Shift;
        if (_win.Checked) modifiers |= HotkeyModifiers.Meta;

        var binding = new HotkeyBinding(modifiers, (string)_key.SelectedItem!);

        // Saving an unregistrable combination would leave the hotkey silently dead; the default is
        // better than that, and the warning below says so before Save is pressed.
        return binding.IsValid ? binding : HotkeyBinding.Default;
    }

    /// <summary>Keeps the dialog honest about what is in effect and what won't be accepted.</summary>
    private void Sync()
    {
        var on = _hotkeyEnabled.Checked;
        foreach (var control in new Control[] { _ctrl, _alt, _shift, _win, _key })
            control.Enabled = on;

        _interval.Enabled = (SyncMode)_syncMode.SelectedItem! == SyncMode.Automatic;

        var needsModifier = on && !(_ctrl.Checked || _alt.Checked || _win.Checked);
        _warning.Text = needsModifier
            ? $"Needs Ctrl, Alt or Win — otherwise {HotkeyBinding.Default} is used."
            : string.Empty;
    }

    private static CheckBox Check(string text, bool value, int top, int left = 14, int width = 260)
        => new() { Text = text, Checked = value, Location = new Point(left, top), Size = new Size(width, 24), AutoSize = false };

    private static Label Caption(string text, int top)
        => new() { Text = text, Location = new Point(14, top), Size = new Size(140, 22) };

    private static ComboBox Choice(Type enumType, object selected, int top)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(160, top),
            Size = new Size(160, 26),
        };
        foreach (var value in Enum.GetValues(enumType))
            combo.Items.Add(value);
        combo.SelectedItem = selected;
        return combo;
    }
}
