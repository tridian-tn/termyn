using System.Text.Json.Nodes;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Core.Sync;

namespace Termyn.Core.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("Ctrl+Alt+A", HotkeyModifiers.Control | HotkeyModifiers.Alt, "A")]
    [InlineData("ctrl+alt+a", HotkeyModifiers.Control | HotkeyModifiers.Alt, "A")]
    [InlineData(" Control + Shift + F5 ", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F5")]
    [InlineData("Win+Space", HotkeyModifiers.Meta, "SPACE")]
    public void Reads_a_written_binding(string text, HotkeyModifiers modifiers, string key)
    {
        Assert.True(HotkeyBinding.TryParse(text, out var binding));
        Assert.Equal(modifiers, binding.Modifiers);
        Assert.Equal(key, binding.Key);
    }

    [Theory]
    [InlineData("A")]                 // no modifier at all
    [InlineData("Shift+A")]           // shift alone is just typing
    [InlineData("Ctrl+Alt")]          // modifiers with nothing to press
    [InlineData("Ctrl+A+B")]          // two keys is two combinations
    [InlineData("Ctrl+F13")]          // outside the offered set
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_a_binding_the_desktop_could_not_take(string? text)
    {
        Assert.False(HotkeyBinding.TryParse(text, out _));
        Assert.Equal(HotkeyBinding.Default, HotkeyBinding.ParseOrDefault(text));
    }

    [Fact]
    public void Round_trips_through_its_written_form()
    {
        var binding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "K");

        Assert.Equal("Ctrl+Alt+Shift+K", binding.ToString());
        Assert.True(HotkeyBinding.TryParse(binding.ToString(), out var reread));
        Assert.Equal(binding, reread);
    }

    [Fact]
    public void The_default_is_the_combination_the_spec_names()
        => Assert.Equal("Ctrl+Alt+A", HotkeyBinding.Default.ToString());
}

public class ThemePaletteTests
{
    [Theory]
    [InlineData(ThemePreference.Light, true, false)]
    [InlineData(ThemePreference.Light, false, false)]
    [InlineData(ThemePreference.Dark, true, true)]
    [InlineData(ThemePreference.Dark, false, true)]
    public void A_chosen_theme_ignores_what_the_desktop_prefers(ThemePreference preference, bool systemLight, bool expectDark)
        => Assert.Equal(expectDark, ThemePalette.For(preference, systemLight).IsDark);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Following_the_system_takes_its_answer(bool systemLight, bool expectDark)
        => Assert.Equal(expectDark, ThemePalette.For(ThemePreference.System, systemLight).IsDark);

    [Fact]
    public void Priority_colours_are_shared_by_both_themes()
    {
        // They match Todoist's, so a task reads the same here as in the web app — and the same in
        // dark as in light, which is why they don't hang off a palette.
        Assert.Equal("#E4483A", ThemePalette.ForPriority(Priority.P1).ToString());
        Assert.Equal("#9AA0AB", ThemePalette.ForPriority(Priority.P4).ToString());
    }

    [Fact]
    public void The_accent_is_amber_on_slate()
    {
        Assert.Equal("#F2A03C", ThemePalette.Dark.Accent.ToString());
        Assert.Equal("#16181D", ThemePalette.Dark.Background.ToString());
        Assert.Equal("#C77D1E", ThemePalette.Light.Accent.ToString());
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("termyn-settings").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Config => Path.Combine(_dir, "config.json");

    [Fact]
    public void With_no_file_at_all_the_defaults_apply()
    {
        var settings = new SettingsStore(Config).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("Ctrl+Alt+A", settings.Hotkey);
        Assert.Equal(ThemePreference.System, settings.Theme);
        Assert.Equal(SyncMode.Automatic, settings.SyncMode);
        Assert.True(settings.CloseToTray);
        Assert.False(settings.LaunchAtLogin);
    }

    [Fact]
    public void Saved_settings_come_back()
    {
        var store = new SettingsStore(Config);
        store.Save(new AppSettings
        {
            Hotkey = "Ctrl+Shift+Q",
            Theme = ThemePreference.Dark,
            SyncMode = SyncMode.Manual,
            SyncIntervalSeconds = 90,
            LaunchAtLogin = true,
            View = new ViewState { SelectedKey = "project:p1", SidebarWidth = 300, CollapsedKeys = ["Projects"] },
        });

        var reread = new SettingsStore(Config).Load();

        Assert.Equal("Ctrl+Shift+Q", reread.Hotkey);
        Assert.Equal(ThemePreference.Dark, reread.Theme);
        Assert.Equal(SyncMode.Manual, reread.SyncMode);
        Assert.Equal(90, reread.SyncIntervalSeconds);
        Assert.True(reread.LaunchAtLogin);
        Assert.Equal("project:p1", reread.View.SelectedKey);
        Assert.Equal(300, reread.View.SidebarWidth);
        Assert.Equal(["Projects"], reread.View.CollapsedKeys);
    }

    [Fact]
    public void Enums_are_written_as_names_so_the_file_can_be_hand_read()
    {
        new SettingsStore(Config).Save(new AppSettings { Theme = ThemePreference.Dark });

        Assert.Contains("\"Dark\"", File.ReadAllText(Config));
    }

    [Fact]
    public void Keys_this_version_does_not_know_survive_a_save()
    {
        File.WriteAllText(Config, """
        {
          "schemaVersion": 1,
          "theme": "Light",
          "somethingLater": { "nested": 7 },
          "view": { "selectedKey": "Today", "laterViewKey": "kept" }
        }
        """);

        var store = new SettingsStore(Config);
        var settings = store.Load();
        store.Save(settings with { Theme = ThemePreference.Dark });

        var written = (JsonObject)JsonNode.Parse(File.ReadAllText(Config))!;
        Assert.Equal("Dark", written["theme"]!.ToString());
        Assert.Equal(7, written["somethingLater"]!["nested"]!.GetValue<int>());
        Assert.Equal("kept", written["view"]!["laterViewKey"]!.ToString());
    }

    [Fact]
    public void A_file_from_a_later_version_keeps_its_version_number()
    {
        // Stamping our own number on it would tell that later version its migrations had already run.
        File.WriteAllText(Config, """{ "schemaVersion": 99, "theme": "Light" }""");

        var store = new SettingsStore(Config);
        store.Save(store.Load());

        var written = (JsonObject)JsonNode.Parse(File.ReadAllText(Config))!;
        Assert.Equal(99, written["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_file_predating_the_version_marker_is_brought_forward()
    {
        File.WriteAllText(Config, """{ "theme": "Dark" }""");

        var settings = new SettingsStore(Config).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(ThemePreference.Dark, settings.Theme);
    }

    [Fact]
    public void An_unreadable_file_is_moved_aside_rather_than_overwritten()
    {
        File.WriteAllText(Config, "{ this is not json");

        var settings = new SettingsStore(Config).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.Equal("{ this is not json", File.ReadAllText(Config + ".bad"));
    }

    [Fact]
    public void A_value_of_the_wrong_shape_falls_back_without_losing_the_file()
    {
        File.WriteAllText(Config, """{ "schemaVersion": 1, "syncIntervalSeconds": "forty-five", "mine": 1 }""");

        var store = new SettingsStore(Config);
        var settings = store.Load();
        Assert.Equal(45, settings.SyncIntervalSeconds); // the default, since the file's value is unusable

        store.Save(settings);
        var written = (JsonObject)JsonNode.Parse(File.ReadAllText(Config))!;
        Assert.Equal(1, written["mine"]!.GetValue<int>());
    }

    [Fact]
    public void A_half_written_file_is_never_left_behind()
    {
        var store = new SettingsStore(Config);
        store.Save(new AppSettings());

        Assert.False(File.Exists(Config + ".tmp"));
    }

    [Fact]
    public void The_directory_is_created_if_it_is_not_there_yet()
    {
        var nested = Path.Combine(_dir, "deeper", "config.json");

        new SettingsStore(nested).Save(new AppSettings());

        Assert.True(File.Exists(nested));
    }
}

public class AppSettingsTests
{
    [Theory]
    [InlineData(1, AppSettings.MinSyncIntervalSeconds)]
    [InlineData(0, AppSettings.MinSyncIntervalSeconds)]
    [InlineData(-30, AppSettings.MinSyncIntervalSeconds)]
    [InlineData(45, 45)]
    [InlineData(9999, AppSettings.MaxSyncIntervalSeconds)]
    public void The_interval_is_held_inside_the_range_the_spec_allows(int stored, int expected)
        => Assert.Equal(expected, new AppSettings { SyncIntervalSeconds = stored }.ClampedInterval);

    [Fact]
    public void Manual_mode_turns_the_timer_off_but_keeps_the_write_debounce()
    {
        var cadence = new AppSettings { SyncMode = SyncMode.Manual }.Cadence;

        Assert.Equal(Timeout.InfiniteTimeSpan, cadence.Interval);
        Assert.Equal(SyncCadence.Default.WriteDebounce, cadence.WriteDebounce);
    }

    [Fact]
    public void Automatic_mode_polls_at_the_clamped_interval()
        => Assert.Equal(TimeSpan.FromSeconds(300), new AppSettings { SyncIntervalSeconds = 100000 }.Cadence.Interval);

    [Fact]
    public void A_hand_edited_hotkey_that_cannot_be_registered_falls_back_to_the_default()
        => Assert.Equal(HotkeyBinding.Default, new AppSettings { Hotkey = "Shift+A" }.HotkeyBinding);
}
