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
    public void A_combination_something_else_already_owns_is_refused()
    {
        // The RegisterHotKey-said-no branch, which is what drives the "another application already
        // owns it" message. The other refusal test never reaches the API at all.
        var binding = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F8");

        using var owner = new WindowsGlobalHotkey();
        Assert.True(owner.Register(binding), "the desktop refused an unlikely combination");

        using var latecomer = new WindowsGlobalHotkey();
        Assert.False(latecomer.Register(binding));
        Assert.Null(latecomer.Current);
    }

    [Fact]
    public void The_window_it_listens_on_is_message_only()
    {
        // Message-only means parented into the HWND_MESSAGE namespace: posted messages and nothing
        // else — no z-order, no taskbar presence, none of the broadcasts a top-level window is sent.
        // HWND_MESSAGE is a pseudo-handle you pass in, so what comes back is the system window that
        // owns that namespace; an ordinary top-level window answers with the desktop instead.
        using var hotkey = new WindowsGlobalHotkey();

        var parent = GetAncestor(hotkey.Handle, GA_PARENT);

        Assert.NotEqual(IntPtr.Zero, parent);
        Assert.NotEqual(GetDesktopWindow(), parent);
    }

    [Fact]
    public void A_press_reaches_the_subscriber()
    {
        // Posted rather than typed: synthesising Ctrl+Alt+Shift+F9 would press it on whatever the
        // developer happens to be doing. This covers the wiring from the window's queue onwards,
        // which is the half the message-only parenting could break; that the desktop accepts the
        // registration at all is covered above.
        using var hotkey = new WindowsGlobalHotkey();
        var pressed = 0;
        hotkey.Pressed += () => pressed++;

        Assert.True(hotkey.Register(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F9")));
        PostMessage(hotkey.Handle, WM_HOTKEY, 1, 0);
        Application.DoEvents();

        Assert.Equal(1, pressed);
    }

    [Fact]
    public void Another_windows_hotkey_id_is_ignored()
    {
        using var hotkey = new WindowsGlobalHotkey();
        var pressed = 0;
        hotkey.Pressed += () => pressed++;

        hotkey.Register(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F9"));
        PostMessage(hotkey.Handle, WM_HOTKEY, 99, 0);
        Application.DoEvents();

        Assert.Equal(0, pressed);
    }

    private const int WM_HOTKEY = 0x0312;
    private const uint GA_PARENT = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, int wParam, int lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [Fact]
    public void Using_it_after_disposal_says_so_rather_than_failing_obscurely()
    {
        var hotkey = new WindowsGlobalHotkey();
        hotkey.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hotkey.Register(HotkeyBinding.Default));
    }
}

public class TrayNotifierTests
{
    [Fact]
    public void A_tooltip_longer_than_the_shell_allows_is_cut_short()
    {
        using var tray = new TrayNotifier();

        // The shell rejects anything over 63 characters outright on older builds.
        tray.SetStatus(new string('x', 200), 0);

        Assert.True(tray.Tooltip.Length <= 63, $"tooltip was {tray.Tooltip.Length} characters");
        Assert.EndsWith("…", tray.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tooltip_that_fits_is_left_alone()
    {
        using var tray = new TrayNotifier();

        tray.SetStatus("Termyn — 3 tasks due today", 3);

        Assert.Equal("Termyn — 3 tasks due today", tray.Tooltip);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    [InlineData(-3)]
    public void Any_count_produces_an_icon(int dueToday)
    {
        using var tray = new TrayNotifier();

        tray.SetStatus("Termyn", dueToday);

        Assert.NotNull(tray.Icon);
    }

    [Fact]
    public void The_tray_icon_is_drawn_at_the_size_the_shell_asks_for()
    {
        using var tray = new TrayNotifier();

        tray.SetStatus("Termyn", 0);

        // Floored at 16: a shell reporting something smaller would otherwise get an icon too small
        // to read the mark in.
        Assert.Equal(Math.Max(SystemInformation.SmallIconSize.Width, 16), tray.Icon!.Width);
    }

    [Fact]
    public void A_count_makes_the_tray_icon_look_different()
    {
        // Every other test here is satisfied by an icon existing, so this is the one that would
        // fail if the badge stopped being drawn.
        using var tray = new TrayNotifier();

        tray.SetStatus("Termyn", 0);
        using var plain = tray.Icon!.ToBitmap();

        tray.SetStatus("Termyn", 3);
        using var badged = tray.Icon!.ToBitmap();

        var differing = 0;
        for (var x = 0; x < plain.Width; x++)
            for (var y = 0; y < plain.Height; y++)
                if (plain.GetPixel(x, y) != badged.GetPixel(x, y))
                    differing++;

        Assert.True(differing > 0, "a badged tray icon should not look like the plain one");
    }

    [Fact]
    public void A_tooltip_is_cut_only_when_it_is_actually_too_long()
    {
        using var tray = new TrayNotifier();

        var exact = new string('x', 63);
        tray.SetStatus(exact, 0);
        Assert.Equal(exact, tray.Tooltip);

        tray.SetStatus(new string('x', 64), 0);
        Assert.Equal(63, tray.Tooltip.Length);
        Assert.EndsWith("…", tray.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Cutting_a_tooltip_never_leaves_half_a_character()
    {
        // The tooltip is built from project names, and Todoist allows emoji in them. Slicing by code
        // unit can split a surrogate pair and hand the shell half a character.
        using var tray = new TrayNotifier();

        tray.SetStatus(new string('x', 61) + "🎯" + new string('y', 20), 0);

        Assert.DoesNotContain(tray.Tooltip, char.IsSurrogate);
    }

    [Fact]
    public void Setting_commands_twice_replaces_rather_than_stacks()
    {
        using var tray = new TrayNotifier();
        var ran = new List<string>();

        tray.SetCommands([new NotifierCommand("Gone", () => ran.Add("Gone"))]);
        tray.SetCommands([new NotifierCommand("First", () => ran.Add("First")), new NotifierCommand("Last", () => ran.Add("Last"))]);

        Assert.Equal(["First", "Last"], tray.MenuLabels);

        // And each item runs the command it is labelled with. Two of them on purpose, and the one
        // invoked is deliberately not the last: capturing the wrong element while wiring the
        // handlers — the usual closure-over-the-loop-variable slip — leaves every label correct and
        // every item firing the last command, and a single-item menu can't tell the two apart.
        tray.Invoke("First");
        Assert.Equal(["First"], ran);
    }

    [Fact]
    public void Nothing_is_asked_of_it_after_disposal()
    {
        var tray = new TrayNotifier();
        tray.SetStatus("Before", 1);
        tray.SetCommands([new NotifierCommand("One", () => { })]);

        tray.Dispose();

        // Both guarded, because the window's own teardown can reach these after the shell is gone.
        tray.SetStatus("After", 3);
        tray.SetCommands([new NotifierCommand("Two", () => { })]);

        // That neither took, rather than merely that neither threw — disposal clears the tooltip
        // itself, so "unchanged" would be asserting the wrong thing, but "the new value didn't
        // land" is exactly what the guards are for. Without them SetStatus goes on to draw, leaking
        // an icon handle during teardown, and nothing here would have noticed.
        Assert.NotEqual("After", tray.Tooltip);
        Assert.DoesNotContain("Two", tray.MenuLabels);
    }

    [Fact]
    public void Replacing_the_tray_icon_releases_the_one_it_replaced()
    {
        // One unmanaged handle per badge change — every task completed, every sync — with no symptom
        // at all until the process runs out of GDI objects hours later.
        using var tray = new TrayNotifier();

        tray.SetStatus("Termyn", 0);
        var first = tray.Icon!;

        tray.SetStatus("Termyn", 3);

        Assert.Throws<ObjectDisposedException>(() => _ = first.Handle);
    }

    [Fact]
    public void The_same_count_twice_does_not_redraw()
    {
        // A redraw per publish is a GDI handle churned on every keystroke.
        using var tray = new TrayNotifier();
        tray.SetStatus("Termyn", 3);
        var first = tray.Icon;

        tray.SetStatus("Termyn — a different tooltip", 3);

        Assert.Same(first, tray.Icon);
    }

    [Fact]
    public void A_negative_count_badges_nothing()
    {
        using var tray = new TrayNotifier();
        tray.SetStatus("Termyn", 0);
        var plain = tray.Icon;

        tray.SetStatus("Termyn", -3);

        // Clamped to zero, so it is the same plain icon rather than a fresh one.
        Assert.Same(plain, tray.Icon);
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
    public void The_pipe_is_listening_by_the_time_the_instance_is_reported_as_taken()
    {
        // Returning true is what tells the rest of startup that this process is the instance, and a
        // second launch can arrive the moment it does. Opened on a queued task, the pipe didn't
        // exist until the pool got round to it, and a launch landing in that gap was lost — it
        // waited two seconds for something to connect to and then exited having done nothing.
        //
        // Asked of the pipe namespace rather than by signalling, so the answer doesn't depend on
        // how quickly anything else runs: this is checked in the statement after the acquire.
        using var holder = new WindowsSingleInstance(Scope());

        Assert.True(holder.TryAcquire());

        Assert.Contains(
            holder.PipeName,
            Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName));
    }

    [Fact]
    public void A_launch_arriving_the_instant_the_instance_is_taken_is_still_heard()
    {
        // The same thing from the other end, and the shape the app actually takes: no sleep between
        // the two, because a second launch doesn't wait either.
        var scope = Scope();
        using var holder = new WindowsSingleInstance(scope);
        Assert.True(holder.TryAcquire());

        using var launcher = new WindowsSingleInstance(scope);

        Assert.True(launcher.TrySignal(InstanceSignals.Show));
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

    [Fact]
    public void The_default_scope_is_this_user_in_this_logon_session()
    {
        using var mine = new WindowsSingleInstance();
        using var explicitly = new WindowsSingleInstance(CurrentSid());

        // The mutex stays in the session namespace: creating a Global\\ object needs a privilege
        // standard users are not granted, and an instance that could never start would be worse
        // than the multi-session gap this leaves.
        Assert.StartsWith(@"Local\Termyn-", mine.MutexName, StringComparison.Ordinal);
        Assert.Equal(explicitly.MutexName, mine.MutexName);
        Assert.Equal(explicitly.PipeName, mine.PipeName);
    }

    [Fact]
    public void A_different_principal_gets_different_names()
    {
        using var mine = new WindowsSingleInstance("S-1-5-21-1-2-3-1001");
        using var theirs = new WindowsSingleInstance("S-1-5-21-1-2-3-1002");

        Assert.NotEqual(mine.MutexName, theirs.MutexName);
        Assert.NotEqual(mine.PipeName, theirs.PipeName);
    }

    [Fact]
    public async Task A_signal_that_arrives_before_anyone_is_listening_is_kept()
    {
        // The listener starts as soon as the instance is acquired, but the window that handles
        // signals doesn't exist until the cache has loaded — with a token dialog in between on a
        // first run. Dropping those told the second launch it had handed over when it hadn't.
        var scope = Scope();
        using var holder = new WindowsSingleInstance(scope);
        Assert.True(holder.TryAcquire());

        using var second = new WindowsSingleInstance(scope);
        Assert.True(second.TrySignal(InstanceSignals.QuickAdd));

        // Give the listener a moment to take it off the wire before anyone subscribes.
        await Task.Delay(500);

        var received = new TaskCompletionSource<string>();
        holder.SignalReceived += m => received.TrySetResult(m);

        Assert.Equal(InstanceSignals.QuickAdd, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_squatted_pipe_name_makes_the_listener_give_up_rather_than_spin()
    {
        // Retrying flat out burned 140% of a core for the life of the process, silently, whenever
        // the name was already taken — by another account, or by a stale process.
        var scope = Scope();
        using var probe = new WindowsSingleInstance(scope, retryDelay: TimeSpan.FromMilliseconds(5));
        using var squatter = new System.IO.Pipes.NamedPipeServerStream(probe.PipeName, System.IO.Pipes.PipeDirection.In, 1);

        Assert.True(probe.TryAcquire());

        // Ten failures at five milliseconds apart; anything that hasn't stopped by now is spinning.
        await probe.Listener!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(probe.Listener.IsCompleted);
    }

    [Fact]
    public async Task A_listener_that_is_working_does_not_give_up()
    {
        var scope = Scope();
        using var holder = new WindowsSingleInstance(scope, retryDelay: TimeSpan.FromMilliseconds(5));
        Assert.True(holder.TryAcquire());

        await Task.Delay(200);

        Assert.False(holder.Listener!.IsCompleted);
    }

    private static string CurrentSid()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    [Fact]
    public async Task A_subscriber_that_throws_does_not_stop_the_next_signal_arriving()
    {
        // The listening loop only catches pipe errors; anything else thrown by a handler would end
        // it for the life of the process.
        var scope = Scope();
        using var holder = new WindowsSingleInstance(scope);
        var second = new TaskCompletionSource<string>();
        var calls = 0;

        holder.SignalReceived += m =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("boom");
            second.TrySetResult(m);
        };
        Assert.True(holder.TryAcquire());

        using var launcher = new WindowsSingleInstance(scope);
        Assert.True(launcher.TrySignal(InstanceSignals.Show));
        await Task.Delay(200);
        Assert.True(launcher.TrySignal(InstanceSignals.QuickAdd));

        Assert.Equal(InstanceSignals.QuickAdd, await second.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_escape_the_replay()
    {
        var scope = Scope();
        using var holder = new WindowsSingleInstance(scope);
        Assert.True(holder.TryAcquire());

        using var launcher = new WindowsSingleInstance(scope);
        Assert.True(launcher.TrySignal(InstanceSignals.Show));
        await Task.Delay(500);

        // The replay runs on the subscribing thread, so a throw would come out of whatever was
        // wiring the handler up — the window's constructor, in the app.
        holder.SignalReceived += _ => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Signals_pile_up_no_further_than_the_buffer_allows()
    {
        var scope = Scope();
        using var holder = new WindowsSingleInstance(scope);
        Assert.True(holder.TryAcquire());

        using var launcher = new WindowsSingleInstance(scope);
        for (var i = 0; i < 30; i++)
            launcher.TrySignal(InstanceSignals.Show);

        await Task.Delay(700);

        var delivered = new List<string>();
        holder.SignalReceived += delivered.Add;

        Assert.InRange(delivered.Count, 1, 8);
    }

    /// <summary>A scope unique to each test, so tests running side by side don't fight over one.</summary>
    private static string Scope() => "termyn-test-" + Guid.NewGuid().ToString("N");
}
