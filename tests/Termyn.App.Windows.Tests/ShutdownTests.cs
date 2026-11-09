using System.Runtime.InteropServices;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Windows asking whether it may shut down, which every window Termyn has must say yes to.
/// </summary>
/// <remarks>
/// Windows sends each top-level window WM_QUERYENDSESSION and waits for the answers, and WinForms
/// answers for a form by raising FormClosing: a form that cancels there is refusing the shutdown,
/// and Windows names Termyn as the app preventing it. These send the message the way Windows does,
/// to windows that exist but aren't on screen — which is where the quick-add box spends most of its
/// life.
///
/// Saying yes is only answering a question. WM_ENDSESSION follows with whether the session really is
/// ending, and it isn't when another app said no.
/// </remarks>
public class ShutdownTests
{
    private const string SettingsName = "termyn-shutdown-tests.json";

    private const int WmClose = 0x0010;
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    [WinFormsFact]
    public void The_quick_add_box_lets_Windows_shut_down()
    {
        // It turned every close into hiding, and so refused every shutdown once it had been summoned.
        using var box = TestWindow.QuickAdd();

        Assert.NotEqual(0, SendMessage(box.Handle, WmQueryEndSession, 0, 0));
    }

    [WinFormsFact]
    public void The_user_closing_the_quick_add_box_still_only_hides_it()
    {
        // The next hotkey press has to find it already built. The X and Alt+F4 both reach the box
        // as SC_CLOSE.
        using var box = TestWindow.QuickAdd();

        SendMessage(box.Handle, WmSysCommand, ScClose, 0);

        Assert.False(box.IsDisposed);
    }

    [WinFormsFact]
    public void A_close_sent_after_the_user_has_closed_the_box_really_closes_it()
    {
        // WinForms keeps the reason from the user's close and hands it to the next one, so a close
        // sent by another program afterwards read as the user's and the box hid again.
        using var box = TestWindow.QuickAdd();
        SendMessage(box.Handle, WmSysCommand, ScClose, 0);

        SendMessage(box.Handle, WmClose, 0, 0);

        Assert.True(box.IsDisposed);
    }

    [WinFormsFact]
    public void A_box_closed_from_outside_is_built_again()
    {
        using var window = TestWindow.Build(SettingsName);
        var first = window.QuickAdd;

        SendMessage(first.Handle, WmClose, 0, 0);
        var second = window.QuickAdd;

        Assert.True(first.IsDisposed);
        Assert.NotSame(first, second);
        Assert.False(second.IsDisposed);
    }

    [WinFormsFact]
    public void The_window_lets_Windows_shut_down_when_it_closes_to_the_tray()
    {
        // Closing to the tray is the default, and it's the user's close it hides on, not this one.
        using var window = TestWindow.Build(SettingsName);

        Assert.NotEqual(0, SendMessage(window.Handle, WmQueryEndSession, 0, 0));
    }

    [WinFormsFact]
    public void A_shutdown_another_app_refuses_leaves_the_window_running()
    {
        // The window has to come out of it still able to fetch completed tasks and check for
        // updates, which it can't once it has stopped its work.
        using var window = TestWindow.Build(SettingsName);

        SendMessage(window.Handle, WmQueryEndSession, 0, 0);
        SendMessage(window.Handle, WmEndSession, 0, 0);

        Assert.False(window.IsDisposed);
        Assert.False(window.Stopped);
    }

    [WinFormsFact]
    public void A_shutdown_that_goes_ahead_stops_the_window()
    {
        using var window = TestWindow.Build(SettingsName);

        SendMessage(window.Handle, WmQueryEndSession, 0, 0);
        SendMessage(window.Handle, WmEndSession, 1, 0);

        Assert.True(window.IsDisposed);
        Assert.True(window.Stopped);
    }
}
