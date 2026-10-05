using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// That the window opens on the view it was left on, or on Today when that's gone.
/// </summary>
/// <remarks>
/// Built as whole windows for the same reason as the folds: the presenter can be asked to open a
/// key and the settings file can hold one, and both answer correctly whether or not the window
/// carries the one to the other.
/// </remarks>
public class ViewAcrossRestartTests : IDisposable
{
    private readonly string _config = Path.Combine(
        Path.GetTempPath(),
        $"termyn-view-{Guid.NewGuid():N}.json");

    private static readonly string Work = SidebarKeys.For(SidebarKind.Project, "p1");

    private static readonly string FollowUp = SidebarKeys.For(SidebarKind.Label, "followup");

    public void Dispose()
    {
        if (File.Exists(_config))
            File.Delete(_config);

        GC.SuppressFinalize(this);
    }

    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"followup","item_order":1}""");
        return store;
    }

    [WinFormsFact]
    public void The_view_left_open_is_the_one_that_opens_next_time()
    {
        using (var window = Shown(new FakeApi(), out _))
        {
            TestWindow.Click(window, Work);
            window.Close();
        }

        using var second = Shown(new FakeApi(), out var presenter);

        Assert.Equal(ViewSelection.OfProject("p1"), presenter.Selection);
        Assert.Equal(Work, TestWindow.Highlighted(second));
    }

    [WinFormsFact]
    public void A_view_is_written_down_as_soon_as_it_is_opened()
    {
        // Not left until the window closes: a process stopped from the debugger or Task Manager
        // never gets that far, and came back on whatever it had been on the time before.
        using var window = Shown(new FakeApi(), out _);

        TestWindow.Click(window, Work);

        Assert.Contains($"\"selectedKey\": \"{Work}\"", Saved());
    }

    [WinFormsFact]
    public void A_view_stepped_to_from_the_keyboard_is_written_down_as_soon_as_it_opens()
    {
        // Unlike a click, Ctrl+↓ only tells the tree which row it's on once the render that writes
        // the view down is over. So does picking a view from the tray.
        using var window = Shown(new FakeApi(), out var presenter);

        window.Run(AppCommand.NextView);

        Assert.Equal(SidebarKeys.For(SidebarKind.SmartView, "Upcoming"), presenter.SelectedKey);
        Assert.Contains($"\"selectedKey\": \"{presenter.SelectedKey}\"", Saved());
    }

    [WinFormsFact]
    public void A_file_that_names_no_view_opens_on_Today()
    {
        File.WriteAllText(_config, """{"view":{"sidebarWidth":240}}""");

        using var window = Shown(new FakeApi(), out var presenter);

        Assert.Equal(ViewSelection.Default, presenter.Selection);
        Assert.Equal(ViewSelection.Default.Key, TestWindow.Highlighted(window));
    }

    [WinFormsFact]
    public void A_view_the_cache_has_never_heard_of_opens_on_Today()
    {
        File.WriteAllText(_config, """{"view":{"selectedKey":"label:gone"}}""");

        using var window = Shown(new FakeApi(), out var presenter);

        Assert.Equal(ViewSelection.Default, presenter.Selection);
        Assert.Equal(ViewSelection.Default.Key, TestWindow.Highlighted(window));
    }

    [WinFormsFact]
    public async Task A_view_the_first_sync_finds_gone_gives_way_to_Today_in_the_sidebar_too()
    {
        // The label was deleted on the phone while the window was closed. The outline moving to
        // Today isn't enough on its own: the tree has to follow it, and so does the file.
        File.WriteAllText(_config, $$$"""{"view":{"selectedKey":"{{{FollowUp}}}"}}""");

        // Offline as it opens, so the label is still in the cache and the restart reopens it.
        var api = new FakeApi { Throw = new TodoistNetworkException("offline") };
        using var window = Shown(api, out var presenter);

        Assert.Equal(ViewSelection.OfLabel("followup"), presenter.Selection);

        api.Throw = null;
        api.Response = new SyncResponse { SyncToken = "s1", Changes = [Json.Deleted("labels", "l1")] };
        await presenter.SyncAsync();

        Assert.Equal(ViewSelection.Default, presenter.Selection);
        Assert.Equal(ViewSelection.Default.Key, TestWindow.Highlighted(window));
        Assert.Contains($"\"selectedKey\": \"{ViewSelection.Default.Key}\"", Saved());
    }

    private string Saved() => File.Exists(_config) ? File.ReadAllText(_config) : string.Empty;

    /// <summary>
    /// A window started from the settings file, shown off the screen.
    /// </summary>
    /// <remarks>
    /// Shown rather than only realised: a form that was never displayed doesn't raise its closing
    /// event, which is one of the places the view is written.
    /// </remarks>
    private MainForm Shown(FakeApi api, out MainPresenter presenter)
    {
        var window = TestWindow.Start(_config, Store(), api, out presenter);
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-32000, -32000);
        window.ShowInTaskbar = false;
        window.Show();
        return window;
    }
}
