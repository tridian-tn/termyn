using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The window over an account Todoist has turned the token away from.
/// </summary>
/// <remarks>
/// What the account does meanwhile is the engine's and the presenter's, and tested there. This is
/// what the window shows of it: the status line that asks for a token, and the menus greyed over an
/// account that can't take a change.
/// </remarks>
public class ReconnectWindowTests
{
    [WinFormsFact]
    public void A_window_opened_over_an_account_with_no_token_asks_for_one_in_the_status_line()
    {
        using var window = TestWindow.Waiting("reconnect-window-status.json", Held(), out var presenter);
        Assert.True(presenter.ReconnectNeeded);

        // A window with no handle has nowhere to render to, and skips it.
        _ = window.Handle;

        // The first sync, as loading the window runs it. With no token it fails before reaching the
        // network, so it's done by the time it returns.
        var sync = presenter.SyncAsync();
        Assert.True(sync.IsFaulted);
        Assert.IsType<TodoistAuthException>(sync.Exception?.InnerException);

        var status = Status(window);
        Assert.Contains("Todoist turned your token away", status.Text);
        Assert.Equal(Cursors.Hand, status.Cursor);
    }

    [WinFormsFact]
    public void The_menus_over_a_waiting_account_offer_reconnecting_and_nothing_that_writes()
    {
        using var window = TestWindow.Waiting("reconnect-window-menus.json", Held(), out _);

        var context = window.NewContext(new SidebarNode(SidebarKind.Project, "p", "Work", 0, "project:p"));

        Assert.True(context.ReconnectNeeded);
        Assert.True(Commands.StateOf(AppCommand.Reconnect, context).Enabled);
        Assert.False(Commands.StateOf(AppCommand.NewTask, context).Enabled);
        Assert.False(Commands.StateOf(AppCommand.RenameSelection, context).Enabled);
        Assert.True(Commands.StateOf(AppCommand.SignOut, context).Enabled);
    }

    [WinFormsFact]
    public void A_signed_in_window_doesnt_offer_to_reconnect()
    {
        using var window = TestWindow.Build("reconnect-window-signed-in.json", Held(), out _, out var presenter);
        _ = window.Handle;

        // The fake server answers at once, so this is done by the time it returns.
        var sync = presenter.SyncAsync();
        Assert.True(sync.IsCompletedSuccessfully);

        var context = window.NewContext(null);

        Assert.False(context.ReconnectNeeded);
        Assert.False(Commands.StateOf(AppCommand.Reconnect, context).Enabled);
        Assert.Contains("Synced", Status(window).Text);
        Assert.Equal(Cursors.Default, Status(window).Cursor);
    }

    /// <summary>The status line along the bottom of the window.</summary>
    private static System.Windows.Forms.Label Status(Form window)
        => TestWindow.Descendants(window).OfType<System.Windows.Forms.Label>().Single(l => l.Parent == window && l.Dock == DockStyle.Bottom);

    /// <summary>Account u1, holding one project with a task in it.</summary>
    private static InMemorySnapshotStore Held()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource(ResourceType.User, ResourceType.User, """{"id":"u1"}""");
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("items", "t1", """{"id":"t1","content":"Plan the week","project_id":"p","child_order":1}""");
        return store;
    }
}
