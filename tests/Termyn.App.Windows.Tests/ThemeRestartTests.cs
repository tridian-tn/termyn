using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Saving a new theme, which the window doesn't paint on but offers as a restart.
/// </summary>
/// <remarks>
/// What Windows draws only takes a theme as Termyn starts, so painting the rest left the window half
/// in each. These check that the window stays wholly in the theme it was built in, that the new one
/// is saved for the next start, and that the restart is asked about once and closes the window the
/// way Exit does when it's taken.
/// </remarks>
public class ThemeRestartTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"termyn-theme-restart-{Guid.NewGuid():N}");

    private string Config => Path.Combine(_folder, "config.json");

    public ThemeRestartTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A window started from a settings file that says dark.</summary>
    private MainForm DarkWindow() => WindowIn(ThemePreference.Dark);

    /// <summary>A window started from a settings file that names a theme.</summary>
    private MainForm WindowIn(ThemePreference theme)
    {
        Assert.True(new SettingsStore(Config).Save(new AppSettings { Theme = theme }));
        return TestWindow.Start(Config, new InMemorySnapshotStore(), new FakeApi(), out _);
    }

    private static Color DarkBackground => Theme.From(ThemePalette.Dark).Background;

    [WinFormsFact]
    public void Choosing_later_keeps_the_window_in_its_theme_and_saves_the_new_one_for_the_next_start()
    {
        using var window = DarkWindow();
        var asked = 0;
        window.AskToRestart = () =>
        {
            asked++;
            return false;
        };
        Assert.Equal(DarkBackground, window.BackColor);

        window.ApplySettings(new AppSettings { Theme = ThemePreference.Light });

        Assert.Equal(1, asked);
        Assert.Equal(DarkBackground, window.BackColor);
        Assert.False(window.RestartRequested);
        Assert.False(window.IsDisposed);
        Assert.Contains(Strings.NoticeThemeAtNextStart, TestWindow.StatusLine(window).Text, StringComparison.Ordinal);
        Assert.Equal(ThemePreference.Light, new SettingsStore(Config).Load().Theme);
    }

    [WinFormsFact]
    public void Choosing_to_restart_closes_the_window_with_the_new_theme_saved()
    {
        using var window = DarkWindow();
        TestWindow.ShowOffScreen(window);
        window.AskToRestart = () => true;

        window.ApplySettings(new AppSettings { Theme = ThemePreference.Light });

        Assert.True(window.RestartRequested);
        Assert.False(window.SignedOut);
        Assert.False(window.Visible);
        Assert.Equal(ThemePreference.Light, new SettingsStore(Config).Load().Theme);
    }

    [WinFormsFact]
    public void Choosing_to_restart_closes_the_window_even_when_it_closes_to_the_tray()
    {
        // The tray is for the user's own close. Left to it, the window would hide and nothing would
        // ever start again.
        Assert.True(new SettingsStore(Config).Save(new AppSettings { Theme = ThemePreference.Dark, CloseToTray = true }));
        using var window = TestWindow.Start(Config, new InMemorySnapshotStore(), new FakeApi(), out _);
        TestWindow.ShowOffScreen(window);
        window.AskToRestart = () => true;

        window.ApplySettings(new AppSettings { Theme = ThemePreference.Light, CloseToTray = true });

        Assert.True(window.RestartRequested);
        Assert.True(window.IsDisposed);
    }

    [WinFormsFact]
    public void Going_back_to_the_theme_on_screen_asks_nothing_and_says_nothing()
    {
        using var window = DarkWindow();
        var asked = 0;
        window.AskToRestart = () =>
        {
            asked++;
            return false;
        };
        window.ApplySettings(new AppSettings { Theme = ThemePreference.Light });

        window.ApplySettings(new AppSettings { Theme = ThemePreference.Dark });

        Assert.Equal(1, asked);
        Assert.DoesNotContain(Strings.NoticeThemeAtNextStart, TestWindow.StatusLine(window).Text, StringComparison.Ordinal);
        Assert.Equal(ThemePreference.Dark, new SettingsStore(Config).Load().Theme);
    }

    [WinFormsFact]
    public void Saving_something_else_meanwhile_says_the_theme_is_still_to_come_without_asking_again()
    {
        using var window = DarkWindow();
        var asked = 0;
        window.AskToRestart = () =>
        {
            asked++;
            return false;
        };
        window.ApplySettings(new AppSettings { Theme = ThemePreference.Light });

        window.ApplySettings(new AppSettings { Theme = ThemePreference.Light, ShowPriorityTips = false });

        Assert.Equal(1, asked);
        Assert.Contains(Strings.NoticeThemeAtNextStart, TestWindow.StatusLine(window).Text, StringComparison.Ordinal);
    }

    [WinFormsFact]
    public void A_theme_that_comes_out_the_same_as_the_one_on_screen_asks_nothing()
    {
        // Following the system on a dark desktop is dark, so naming dark changes nothing anyone
        // would see. Whichever way this machine is set, the theme naming it is the same one.
        using var window = WindowIn(ThemePreference.System);
        var asked = 0;
        window.AskToRestart = () =>
        {
            asked++;
            return false;
        };
        var same = Theme.SystemPrefersLight() ? ThemePreference.Light : ThemePreference.Dark;
        var other = same == ThemePreference.Light ? ThemePreference.Dark : ThemePreference.Light;

        window.ApplySettings(new AppSettings { Theme = same });

        Assert.Equal(0, asked);
        Assert.DoesNotContain(Strings.NoticeThemeAtNextStart, TestWindow.StatusLine(window).Text, StringComparison.Ordinal);
        Assert.Equal(same, new SettingsStore(Config).Load().Theme);

        window.ApplySettings(new AppSettings { Theme = other });

        Assert.Equal(1, asked);
    }

    [WinFormsFact]
    public void Another_setting_alone_asks_nothing()
    {
        using var window = DarkWindow();
        window.AskToRestart = () => throw new InvalidOperationException("Asked to restart without a theme change.");

        window.ApplySettings(new AppSettings { Theme = ThemePreference.Dark, ShowPriorityTips = false });

        Assert.False(window.RestartRequested);
        Assert.DoesNotContain(Strings.NoticeThemeAtNextStart, TestWindow.StatusLine(window).Text, StringComparison.Ordinal);
    }

    [WinFormsFact]
    public void A_theme_that_couldnt_be_saved_isnt_offered_as_a_restart()
    {
        // The next copy reads the theme from the file, so restarting would only come back in the
        // same one. A folder where the file should be can be read as no settings but not written.
        Directory.CreateDirectory(Config);
        using var window = TestWindow.Start(Config, new InMemorySnapshotStore(), new FakeApi(), out _);
        window.AskToRestart = () => throw new InvalidOperationException("Offered a restart into a theme that wasn't saved.");

        // Opposite to this machine's own, since the window follows the system with no file to read.
        window.ApplySettings(new AppSettings { Theme = Theme.SystemPrefersLight() ? ThemePreference.Dark : ThemePreference.Light });

        Assert.False(window.RestartRequested);
        Assert.Contains(Strings.NoticeSettingsNotSaved, TestWindow.StatusLine(window).Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.NoticeThemeAtNextStart, TestWindow.StatusLine(window).Text, StringComparison.Ordinal);
    }
}
