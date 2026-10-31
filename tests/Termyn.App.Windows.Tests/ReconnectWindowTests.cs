using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

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

    [WinFormsFact]
    public async Task A_description_typed_before_the_token_went_is_kept_for_its_own_task()
    {
        // It can't be written while there's no token, and moving to another task used to open that
        // one's description over it, losing what had been typed.
        var store = Held();
        store.PutResource("items", "t1", """{"id":"t1","content":"Plan the week","description":"Notes","project_id":"p","child_order":1}""");
        store.PutResource("items", "t2", """{"id":"t2","content":"Book the venue","project_id":"p","child_order":2}""");
        var api = new FakeApi();
        var settings = Path.Combine(Path.GetTempPath(), $"reconnect-window-description-{Guid.NewGuid():N}.json");

        try
        {
            using var window = TestWindow.Start(settings, store, api, out var presenter);
            _ = window.Handle;
            presenter.Select(ViewSelection.OfProject("p"));
            window.ShowPanelTab(comments: false);

            var outline = TestWindow.Find<OutlineView>(window);
            var editor = TestWindow.Find<MarkdownEditor>(window);
            _ = outline.Handle;
            _ = editor.Handle;

            outline.SelectId("t1");
            editor.Text = "Notes, and more";

            // Turned away before the typing paused long enough to be saved.
            api.Throw = new TodoistAuthException("rejected");
            try
            {
                await presenter.SyncAsync();
            }
            catch (TodoistAuthException)
            {
                // What a rejected token is reported as; the engine has let go of it either way.
            }

            Assert.True(presenter.ReconnectNeeded);

            outline.SelectId("t2");
            Assert.Equal(string.Empty, editor.Text);

            outline.SelectId("t1");
            Assert.Equal("Notes, and more", editor.Text);

            // And once there's a token again, it's written to the task it was typed into.
            outline.SelectId("t2");
            api.Throw = null;
            presenter.Reconnect("tok2", "u1");
            outline.SelectId("t1");

            Assert.Equal("Notes, and more", presenter.DescriptionOf(SubjectKind.Task, "t1"));
        }
        finally
        {
            File.Delete(settings);
        }
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
