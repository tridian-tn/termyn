using Microsoft.Win32;
using Termyn.Core.Model;
using Termyn.Core.Settings;

// Both namespaces have a Label, and where controls are being themed the control is what it means.
using Label = System.Windows.Forms.Label;

namespace Termyn.App.Windows;

/// <summary>
/// The resolved palette as WinForms colours, and the code that paints it onto a control tree.
/// </summary>
/// <remarks>
/// WinForms has no theme system, so this walks the controls. The framework's own colour mode is set
/// alongside it, which is what turns the scrollbars, menus and title bars dark — those are drawn by
/// the OS and can't be reached from here.
/// </remarks>
internal sealed record Theme(
    bool IsDark,
    Color Background,
    Color Panel,
    Color Row,
    Color Border,
    Color Text,
    Color Muted,
    Color Accent,
    Color AccentHover)
{
    /// <summary>The text drawn on an accent-coloured background — the selected row.</summary>
    public Color OnAccent => IsDark ? Background : Color.White;

    public static Theme From(ThemePalette palette) => new(
        palette.IsDark,
        ToColor(palette.Background),
        ToColor(palette.Panel),
        ToColor(palette.Row),
        ToColor(palette.Border),
        ToColor(palette.TextPrimary),
        ToColor(palette.TextSecondary),
        ToColor(palette.Accent),
        ToColor(palette.AccentHover));

    public static Theme Resolve(ThemePreference preference)
        => From(ThemePalette.For(preference, SystemPrefersLight()));

    public static Color ForPriority(Priority priority) => ToColor(ThemePalette.ForPriority(priority));

    /// <summary>
    /// Whether the desktop is set to light. Windows reports this per-app rather than system-wide, and
    /// a missing value means light — which is what Windows itself assumes.
    /// </summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Tells WinForms which colour mode to draw its own chrome in. Must be called before the first
    /// window exists.
    /// </summary>
    public static void ApplyToFramework(ThemePreference preference)
    {
        // The colour mode API is still marked experimental, but it is the only way to reach the
        // OS-drawn parts — scrollbars, menus, the title bar. Without it a dark theme stops at the
        // edge of every control.
#pragma warning disable WFO5001
        Application.SetColorMode(preference switch
        {
            ThemePreference.Light => SystemColorMode.Classic,
            ThemePreference.Dark => SystemColorMode.Dark,
            _ => SystemColorMode.System,
        });
#pragma warning restore WFO5001
    }

    /// <summary>Paints this theme onto a control and everything inside it.</summary>
    public void Apply(Control control)
    {
        switch (control)
        {
            case Form form:
                form.BackColor = Background;
                form.ForeColor = Text;
                break;

            case TextBox box:
                box.BackColor = Panel;
                box.ForeColor = Text;
                box.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ListBox list:
                list.BackColor = Panel;
                list.ForeColor = Text;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;

            case TreeView tree:
                tree.BackColor = Panel;
                tree.ForeColor = Text;
                tree.LineColor = Border;
                break;

            case ListView view:
                view.BackColor = Panel;
                view.ForeColor = Text;
                break;

            case LinkLabel link:
                link.BackColor = Background;
                link.ForeColor = Muted;
                link.LinkColor = Accent;
                link.ActiveLinkColor = AccentHover;
                link.VisitedLinkColor = Accent;
                break;

            case Button button:
                button.BackColor = Row;
                button.ForeColor = Text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                break;

            case CheckBox or RadioButton or Label:
                control.BackColor = Background;
                control.ForeColor = Text;
                break;

            case ComboBox combo:
                combo.BackColor = Panel;
                combo.ForeColor = Text;
                combo.FlatStyle = FlatStyle.Flat;
                break;

            case NumericUpDown spinner:
                spinner.BackColor = Panel;
                spinner.ForeColor = Text;
                spinner.BorderStyle = BorderStyle.FixedSingle;
                break;

            case SplitContainer split:
                split.BackColor = Border;
                split.Panel1.BackColor = Background;
                split.Panel2.BackColor = Background;
                break;

            default:
                control.BackColor = Background;
                control.ForeColor = Text;
                break;
        }

        foreach (Control child in control.Controls)
            Apply(child);
    }

    private static Color ToColor(Rgb rgb) => Color.FromArgb(rgb.R, rgb.G, rgb.B);
}
