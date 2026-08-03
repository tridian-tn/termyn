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

    /// <summary>Internal rather than private so a test can lay one out without a screen to show it on.</summary>
    internal SettingsForm(AppSettings settings, Theme theme)
    {
        Text = "Termyn — settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(Width_, 404);

        var binding = settings.HotkeyBinding;

        _hotkeyEnabled = Check("Global quick-add hotkey", settings.HotkeyEnabled, 16);
        _ctrl = Modifier("Ctrl", binding.Modifiers.HasFlag(HotkeyModifiers.Control));
        _alt = Modifier("Alt", binding.Modifiers.HasFlag(HotkeyModifiers.Alt));
        _shift = Modifier("Shift", binding.Modifiers.HasFlag(HotkeyModifiers.Shift));
        _win = Modifier("Win", binding.Modifiers.HasFlag(HotkeyModifiers.Meta));

        _key = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 110,
            Margin = new Padding(8, 0, 0, 0),
        };
        foreach (var name in HotkeyBinding.AllowedKeys)
            _key.Items.Add(name);
        _key.SelectedItem = binding.Key;
        if (_key.SelectedIndex < 0)
            _key.SelectedIndex = 0;

        // Flowed rather than placed. The four boxes were positioned and sized to the pixel, which
        // held only for the font they were measured against — under anything wider "Shift" and
        // "Win" lost their last letters. Sized to their own text now, and laid end to end, so the
        // row comes out right whatever the font or the scaling.
        var modifiers = new FlowLayoutPanel
        {
            Location = new Point(38, 46),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        modifiers.Controls.AddRange([_ctrl, _alt, _shift, _win, _key]);

        _warning = new Label
        {
            Location = new Point(16, 88),
            Size = new Size(Width_ - 32, 22),
            ForeColor = theme.Accent,
        };

        _theme = Choice(typeof(ThemePreference), settings.Theme, 134);
        _syncMode = Choice(typeof(SyncMode), settings.SyncMode, 178);

        _interval = new NumericUpDown
        {
            Location = new Point(CaptionWidth + 24, 222),
            Size = new Size(100, 26),
            Minimum = AppSettings.MinSyncIntervalSeconds,
            Maximum = AppSettings.MaxSyncIntervalSeconds,
            Value = settings.ClampedInterval,
        };

        _launchAtLogin = Check("Start Termyn when I sign in", settings.LaunchAtLogin, 268);

        _closeToTray = Check("Closing the window leaves Termyn in the tray", settings.CloseToTray, 300);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(Width_ - 216, 352), Size = new Size(96, 32) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(Width_ - 112, 352), Size = new Size(96, 32) };

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange(
        [
            _hotkeyEnabled, modifiers, _warning,
            Caption("Theme", 138), _theme,
            Caption("Background sync", 182), _syncMode,
            Caption("Every (seconds)", 226), _interval,
            _launchAtLogin, _closeToTray, ok, cancel,
        ]);

        _hotkeyEnabled.CheckedChanged += (_, _) => Sync();
        _syncMode.SelectedIndexChanged += (_, _) => Sync();
        foreach (var box in new[] { _ctrl, _alt, _win })
            box.CheckedChanged += (_, _) => Sync();

        theme.Apply(this);
        _warning.ForeColor = theme.Accent;
        Sync();
    }

    /// <summary>Shows the dialog and returns the amended settings, or null if cancelled.</summary>
    /// <remarks>
    /// Whether launch-at-login can actually be set is the platform layer's to know, and it says so by
    /// refusing — the caller reports that rather than the dialog second-guessing it here.
    /// </remarks>
    public static AppSettings? Edit(IWin32Window owner, AppSettings settings, Theme theme)
    {
        using var form = new SettingsForm(settings, theme);
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

    /// <summary>How wide the dialog is, which the things laid out in it are measured against.</summary>
    private const int Width_ = 560;

    /// <summary>Room for a caption before the control it labels.</summary>
    private const int CaptionWidth = 176;

    // Everything below sizes itself to its own text. Only where a control sits is decided here;
    // how much room its words need is the framework's to work out, and it doesn't get that wrong.

    private static CheckBox Check(string text, bool value, int top)
        => new() { Text = text, Checked = value, Location = new Point(16, top), AutoSize = true };

    /// <summary>A modifier box, whose place is the flow row's to decide rather than ours.</summary>
    private static CheckBox Modifier(string text, bool value)
        => new() { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 5, 16, 3) };

    private static Label Caption(string text, int top)
        => new() { Text = text, Location = new Point(16, top), Size = new Size(CaptionWidth, 22), AutoEllipsis = true };

    private static ComboBox Choice(Type enumType, object selected, int top)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(CaptionWidth + 24, top),
            Size = new Size(200, 26),
        };
        foreach (var value in Enum.GetValues(enumType))
            combo.Items.Add(value);
        combo.SelectedItem = selected;
        return combo;
    }
}
