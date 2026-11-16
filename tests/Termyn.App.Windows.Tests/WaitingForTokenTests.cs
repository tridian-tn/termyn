using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The window over an account whose token is saved but can't be read just now, because another
/// program has its file open.
/// </summary>
/// <remarks>
/// Whether to wait, and for how long, is the presenter's, and tested there. This is what the window
/// shows of it, driven through a real sync rather than told about one.
/// </remarks>
public class WaitingForTokenTests
{
    [WinFormsFact]
    public async Task A_token_another_program_has_open_is_waited_for_quietly_until_a_sync_gets_through()
    {
        var secrets = new FakeSecrets { Stored = "tok" };
        var log = new RecordingLog();
        using var window = TestWindow.WithSecrets("waiting-for-token.json", Held(), secrets, log, out var presenter);
        _ = window.Handle;
        var status = Status(window);
        await presenter.SyncAsync(TestContext.Current.CancellationToken);
        var muted = status.ForeColor;

        // Out of reach, a sync doesn't throw, so there's nothing for the window to report.
        secrets.Unreachable = true;
        await presenter.SyncAsync(TestContext.Current.CancellationToken);

        Assert.EndsWith("Waiting to read the saved Todoist token…", status.Text);
        Assert.NotEqual(muted, status.ForeColor);
        Assert.DoesNotContain("ERROR", log.All);

        secrets.Unreachable = false;
        await presenter.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Synced", status.Text);
        Assert.Equal(muted, status.ForeColor);
    }

    [WinFormsFact]
    public async Task Showing_completed_tasks_while_the_token_is_out_of_reach_says_why_it_didnt()
    {
        // Something the user asked for, which was quietly turned into the background wait.
        var secrets = new FakeSecrets { Stored = "tok" };
        using var window = TestWindow.WithSecrets("waiting-for-token-completed.json", Held(), secrets, new RecordingLog(), out _);
        _ = window.Handle;
        secrets.Unreachable = true;

        window.Run(AppCommand.ToggleCompleted);
        await WaitFor(() => !Status(window).Text.StartsWith("Fetching", StringComparison.Ordinal));

        Assert.Equal("Something went wrong: The saved Todoist token can't be read just now. Try again in a moment.", Status(window).Text);
    }

    /// <summary>Lets the window's own awaits finish, for a command that's started and not awaited.</summary>
    /// <param name="condition">What to wait for</param>
    private static async Task WaitFor(Func<bool> condition)
    {
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < giveUp)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
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
