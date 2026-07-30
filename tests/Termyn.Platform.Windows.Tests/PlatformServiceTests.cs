using Microsoft.Win32;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Platform.Windows;

namespace Termyn.Platform.Windows.Tests;

public class GlobalHotkeyTests
{
    [Theory]
    [InlineData("A", 0x41)]
    [InlineData("Z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("7", 0x37)]
    [InlineData("F5", 0x74)]
    [InlineData("SPACE", 0x20)]
    [InlineData("PAGEUP", 0x21)]
    [InlineData("UP", 0x26)]
    [InlineData("INSERT", 0x2D)]
    public void Maps_every_offered_key_to_its_virtual_key_code(string name, int expected)
        => Assert.Equal((uint)expected, WindowsGlobalHotkey.ToVirtualKey(name));

    [Fact]
    public void Every_key_the_settings_screen_offers_can_actually_be_registered()
    {
        // A name in the list that the platform can't map would be a combination the user can pick
        // and Termyn then silently fails to take.
        var unmappable = HotkeyBinding.AllowedKeys.Where(k => WindowsGlobalHotkey.ToVirtualKey(k) is null).ToList();

        Assert.Empty(unmappable);
    }

    [Theory]
    [InlineData("NOTAKEY")]
    [InlineData("")]
    public void Refuses_a_name_it_cannot_map(string name)
        => Assert.Null(WindowsGlobalHotkey.ToVirtualKey(name));

    [Fact]
    public void A_registered_hotkey_is_reported_and_given_back_again()
    {
        using var hotkey = new WindowsGlobalHotkey();

        // Deliberately obscure: a combination something else on the machine already owns would fail
        // to register, and this test would then be about the wrong thing.
        var binding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F9");
        Assert.True(hotkey.Register(binding), "the desktop refused an unlikely combination");
        Assert.Equal(binding, hotkey.Current);

        hotkey.Unregister();
        Assert.Null(hotkey.Current);
    }

    [Fact]
    public void A_combination_the_desktop_would_not_take_is_refused_rather_than_pretended()
    {
        using var hotkey = new WindowsGlobalHotkey();

        // No modifier, so it never reaches RegisterHotKey — the binding itself is unregistrable.
        Assert.False(hotkey.Register(new HotkeyBinding(HotkeyModifiers.None, "A")));
        Assert.Null(hotkey.Current);
    }

    [Fact]
    public void Registering_twice_replaces_rather_than_stacks()
    {
        using var hotkey = new WindowsGlobalHotkey();
        var first = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F9");
        var second = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F10");

        Assert.True(hotkey.Register(first));
        Assert.True(hotkey.Register(second));

        Assert.Equal(second, hotkey.Current);
    }

    [Fact]
    public void Using_it_after_disposal_says_so_rather_than_failing_obscurely()
    {
        var hotkey = new WindowsGlobalHotkey();
        hotkey.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hotkey.Register(HotkeyBinding.Default));
    }
}

public class AutoStartTests : IDisposable
{
    /// <summary>Somewhere of our own: a test has no business adding a real startup entry.</summary>
    private const string TestRoot = @"Software\Termyn.Tests";

    private const string TestKey = TestRoot + @"\Run";

    /// <summary>Takes the root with it, so a test run leaves nothing on the machine at all.</summary>
    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Nothing to clean up, or not ours to clean.
        }
    }

    [Fact]
    public void Off_by_default()
        => Assert.False(Service().IsEnabled);

    [Fact]
    public void Turning_it_on_and_off_round_trips()
    {
        var service = Service();

        Assert.True(service.SetEnabled(true));
        Assert.True(service.IsEnabled);

        Assert.True(service.SetEnabled(false));
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void The_entry_starts_termyn_in_the_tray()
    {
        Service(@"C:\Programs\Termyn\Termyn.exe").SetEnabled(true);

        using var key = Registry.CurrentUser.OpenSubKey(TestKey);
        var command = key!.GetValue("Termyn") as string;

        // Quoted, because the path can contain spaces — and asking for the tray, because signing in
        // is not a request to be shown a task list.
        Assert.Equal("\"C:\\Programs\\Termyn\\Termyn.exe\" --tray", command);
    }

    [Fact]
    public void Turning_it_on_again_repairs_a_stale_path()
    {
        Service(@"C:\Old\Termyn.exe").SetEnabled(true);

        Service(@"C:\New\Termyn.exe").SetEnabled(true);

        using var key = Registry.CurrentUser.OpenSubKey(TestKey);
        Assert.Contains(@"C:\New\Termyn.exe", (string)key!.GetValue("Termyn")!);
    }

    [Fact]
    public void With_no_binary_to_name_it_refuses_to_enable_but_can_still_clear()
    {
        Service(@"C:\Programs\Termyn\Termyn.exe").SetEnabled(true);
        var blind = Service(null);

        Assert.False(blind.SetEnabled(true));
        Assert.True(blind.SetEnabled(false));
        Assert.False(blind.IsEnabled);
    }

    [Fact]
    public void Turning_off_something_that_was_never_on_is_not_a_failure()
        => Assert.True(Service().SetEnabled(false));

    private static WindowsAutoStart Service(string? path = @"C:\Programs\Termyn\Termyn.exe")
        => new(path, TestKey);
}

public class SingleInstanceTests
{
    [Fact]
    public void The_first_process_gets_the_session_and_the_second_does_not()
    {
        var scope = Scope();
        using var first = new WindowsSingleInstance(scope);
        using var second = new WindowsSingleInstance(scope);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void Acquiring_twice_from_the_same_instance_is_idempotent()
    {
        using var instance = new WindowsSingleInstance(Scope());

        Assert.True(instance.TryAcquire());
        Assert.True(instance.TryAcquire());
    }

    [Fact]
    public void A_different_user_gets_a_session_of_their_own()
    {
        using var mine = new WindowsSingleInstance(Scope());
        using var theirs = new WindowsSingleInstance(Scope());

        Assert.True(mine.TryAcquire());
        Assert.True(theirs.TryAcquire());
    }

    [Fact]
    public async Task A_signal_reaches_the_instance_holding_the_session()
    {
        var scope = Scope();
        using var holder = new WindowsSingleInstance(scope);
        var received = new TaskCompletionSource<string>();
        holder.SignalReceived += m => received.TrySetResult(m);
        Assert.True(holder.TryAcquire());

        using var second = new WindowsSingleInstance(scope);
        Assert.False(second.TryAcquire());
        Assert.True(second.TrySignal(InstanceSignals.QuickAdd));

        Assert.Equal(InstanceSignals.QuickAdd, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_listener_survives_one_signal_and_takes_the_next()
    {
        var scope = Scope();
        using var holder = new WindowsSingleInstance(scope);
        var messages = new List<string>();
        var twice = new TaskCompletionSource();
        holder.SignalReceived += m =>
        {
            lock (messages)
            {
                messages.Add(m);
                if (messages.Count == 2)
                    twice.TrySetResult();
            }
        };
        Assert.True(holder.TryAcquire());

        using var second = new WindowsSingleInstance(scope);
        Assert.True(second.TrySignal(InstanceSignals.Show));
        Assert.True(second.TrySignal(InstanceSignals.QuickAdd));

        await twice.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (messages)
            Assert.Equal([InstanceSignals.Show, InstanceSignals.QuickAdd], messages);
    }

    [Fact]
    public void Signalling_nothing_reports_that_nothing_answered()
    {
        using var lonely = new WindowsSingleInstance(Scope());

        // No holder, so there is no pipe to connect to.
        Assert.False(lonely.TrySignal(InstanceSignals.Show));
    }

    [Fact]
    public void Releasing_the_session_lets_the_next_process_have_it()
    {
        var scope = Scope();
        var first = new WindowsSingleInstance(scope);
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var second = new WindowsSingleInstance(scope);
        Assert.True(second.TryAcquire());
    }

    /// <summary>A scope unique to each test, so tests running side by side don't fight over one.</summary>
    private static string Scope() => "termyn-test-" + Guid.NewGuid().ToString("N");
}
