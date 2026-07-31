using Termyn.Core.Platform;
using Termyn.Core.Settings;

namespace Termyn.Core.Tests;

/// <summary>
/// Who wins when Termyn's settings and the machine disagree about launching at login. The installer
/// writes the same registry entry the app does, so on any given start either could be the truth.
/// </summary>
public class StartupReconciliationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("termyn-startup").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Config => Path.Combine(_dir, "config.json");

    [Fact]
    public void A_first_run_adopts_the_startup_entry_the_installer_wrote()
    {
        // The bug this exists for: the installer ticks "start when I sign in", and the app's first
        // run asserted its own default over it and deleted the entry before the user saw the setting.
        var autoStart = new FakeAutoStart { IsEnabled = true };
        var store = new SettingsStore(Config);
        var settings = store.Load();

        var settled = StartupReconciliation.OnLaunch(store, settings, autoStart);

        Assert.True(settled.LaunchAtLogin);
        Assert.Empty(autoStart.Calls);
        Assert.True(new SettingsStore(Config).Load().LaunchAtLogin);
    }

    [Fact]
    public void A_first_run_with_no_startup_entry_leaves_the_setting_off()
    {
        var autoStart = new FakeAutoStart { IsEnabled = false };
        var store = new SettingsStore(Config);

        var settled = StartupReconciliation.OnLaunch(store, store.Load(), autoStart);

        Assert.False(settled.LaunchAtLogin);
        Assert.Empty(autoStart.Calls);
    }

    [Fact]
    public void A_later_run_asserts_the_setting_over_whatever_the_machine_says()
    {
        // Repairs an entry a startup manager removed, or one left pointing at an old install.
        new SettingsStore(Config).Save(new AppSettings { LaunchAtLogin = true });

        var autoStart = new FakeAutoStart { IsEnabled = false };
        var store = new SettingsStore(Config);

        StartupReconciliation.OnLaunch(store, store.Load(), autoStart);

        Assert.Equal([true], autoStart.Calls);
    }

    [Fact]
    public void A_later_run_turns_it_off_when_the_settings_say_so()
    {
        new SettingsStore(Config).Save(new AppSettings { LaunchAtLogin = false });

        var autoStart = new FakeAutoStart { IsEnabled = true };
        var store = new SettingsStore(Config);

        StartupReconciliation.OnLaunch(store, store.Load(), autoStart);

        Assert.Equal([false], autoStart.Calls);
    }

    [Fact]
    public void A_settings_file_we_could_not_read_does_not_cost_the_user_their_startup_entry()
    {
        // The settings that come back are our defaults, not the user's wishes — asserting them would
        // delete a startup entry over a file that may be perfectly good and merely busy.
        File.WriteAllText(Config, """{ "schemaVersion": 1, "launchAtLogin": true }""");
        var autoStart = new FakeAutoStart { IsEnabled = true };
        var store = new SettingsStore(Config);

        AppSettings settings;
        using (File.Open(Config, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings = store.Load();
        }

        var settled = StartupReconciliation.OnLaunch(store, settings, autoStart);

        Assert.Empty(autoStart.Calls);
        Assert.True(settled.LaunchAtLogin); // taken from the machine, which is the better evidence
    }

    [Fact]
    public void A_typo_in_one_setting_does_not_turn_off_launching_at_login()
    {
        // A file that can't be parsed is moved aside, so this is a first run — and adopting is what
        // stops an unrelated hand-edit silently deleting the startup entry.
        File.WriteAllText(Config, "{ this is not json");
        var autoStart = new FakeAutoStart { IsEnabled = true };
        var store = new SettingsStore(Config);

        var settled = StartupReconciliation.OnLaunch(store, store.Load(), autoStart);

        Assert.Empty(autoStart.Calls);
        Assert.True(settled.LaunchAtLogin);
    }

    [Fact]
    public void A_first_run_that_cannot_write_its_settings_still_starts()
    {
        var readOnly = Path.Combine(_dir, "nested");
        Directory.CreateDirectory(readOnly);
        var store = new SettingsStore(Path.Combine(readOnly, "config.json"));
        store.Load();

        // A directory where the file should be: the write fails, and that must not stop the launch.
        Directory.CreateDirectory(Path.Combine(readOnly, "config.json"));

        var settled = StartupReconciliation.OnLaunch(store, new AppSettings(), new FakeAutoStart { IsEnabled = true });

        Assert.True(settled.LaunchAtLogin);
    }

    [Fact]
    public void A_machine_that_refuses_the_change_does_not_get_to_rewrite_the_setting()
    {
        // A locked-down machine can refuse the write. What must not follow is Termyn concluding the
        // user never wanted it — the setting is the user's, the registry is only where it's kept.
        new SettingsStore(Config).Save(new AppSettings { LaunchAtLogin = true });

        var autoStart = new FakeAutoStart { IsEnabled = false, Refuses = true };
        var store = new SettingsStore(Config);

        var settled = StartupReconciliation.OnLaunch(store, store.Load(), autoStart);

        Assert.Equal([true], autoStart.Calls);
        Assert.True(settled.LaunchAtLogin);
    }

    /// <summary>Records what it was asked to do, since that is the whole question here.</summary>
    private sealed class FakeAutoStart : IAutoStartService
    {
        public List<bool> Calls { get; } = [];

        public bool IsEnabled { get; set; }

        /// <summary>A machine that won't have it — a policy, or a locked-down registry.</summary>
        public bool Refuses { get; init; }

        public bool SetEnabled(bool enabled)
        {
            Calls.Add(enabled);
            if (Refuses)
                return false;

            IsEnabled = enabled;
            return true;
        }
    }
}
