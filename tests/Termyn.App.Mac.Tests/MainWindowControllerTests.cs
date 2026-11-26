using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// Where the keyboard goes as popovers come and go. In the real window, kept invisible, with the
/// waits a popover's animation takes.
/// </summary>
public class MainWindowControllerTests
{
    [AppKitFact]
    public async Task A_popover_replacing_another_keeps_the_keyboard()
    {
        var (window, scheduler) = Open();
        await Settle(0.3);

        window.RunOnTask(AppCommand.Labels, "a");
        await Settle(0.5);
        window.RunOnTask(AppCommand.Deadline, "a");
        await Settle(1.2);

        Assert.NotNull(Field(window.LastPopover!.ContentViewController!.View).CurrentEditor);

        await Close(window, scheduler);
    }

    [AppKitFact]
    public async Task A_popover_gone_gives_the_list_the_keyboard_back()
    {
        var (window, scheduler) = Open();
        await Settle(0.3);

        window.RunOnTask(AppCommand.Deadline, "a");
        await Settle(0.5);
        window.LastPopover!.Close();
        await Settle(1.0);

        Assert.IsAssignableFrom<NSTableView>(window.Window!.FirstResponder);

        await Close(window, scheduler);
    }

    [AppKitFact]
    public async Task A_popover_gone_leaves_the_keyboard_where_a_click_put_it()
    {
        var (window, scheduler) = Open();
        await Settle(0.3);
        var sidebar = Outline(window.Window!.ContentView!)!;

        window.RunOnTask(AppCommand.Deadline, "a");
        await Settle(0.5);
        window.Window.MakeFirstResponder(sidebar);
        window.LastPopover!.Close();
        await Settle(1.0);

        Assert.Same(sidebar, window.Window.FirstResponder);

        await Close(window, scheduler);
    }

    private static (MainWindowController Window, SyncScheduler Scheduler) Open()
    {
        var presenter = FakeAccount.Presenter(plan: ("pro", true));
        var scheduler = new SyncScheduler(ct => presenter.SyncAsync(ct));
        var window = new MainWindowController(presenter, scheduler, new AuthPresenter(new FakeApi(), new FakeSecrets { Stored = "tok" }), new RecordingLog(), _ => { });
        window.Window!.AlphaValue = 0;
        window.ShowWindow(null);
        window.Start(null);
        return (window, scheduler);
    }

    private static async Task Close(MainWindowController window, SyncScheduler scheduler)
    {
        window.Stop();
        window.Close();
        await scheduler.DisposeAsync();
    }

    private static Task Settle(double seconds) => Task.Delay(TimeSpan.FromSeconds(seconds), TestContext.Current.CancellationToken);

    private static NSOutlineView? Outline(NSView view)
        => view as NSOutlineView ?? view.Subviews.Select(Outline).FirstOrDefault(found => found is not null);

    private static NSTextField Field(NSView view) => view.Subviews.OfType<NSTextField>().Single(f => f.Editable);
}
