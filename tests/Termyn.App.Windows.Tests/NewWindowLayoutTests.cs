using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// How a window is laid out before the user has moved anything, and what of their moving it keeps.
/// </summary>
/// <remarks>
/// The outline's columns used to add up to more than the outline the default window gave them, so
/// on a fresh install Labels started past the right-hand edge, and dragging them narrower was
/// forgotten at the next start.
/// </remarks>
public class NewWindowLayoutTests : IDisposable
{
    private readonly string _config = Path.Combine(
        Path.GetTempPath(),
        $"termyn-layout-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_config))
            File.Delete(_config);

        GC.SuppressFinalize(this);
    }

    /// <summary>A project with more tasks than the outline has room for, so it scrolls the way a real one does.</summary>
    private static InMemorySnapshotStore Store()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");

        for (var i = 0; i < 80; i++)
            store.PutResource("items", $"i{i}", $$"""{"id":"i{{i}}","content":"Task {{i}}","project_id":"p","child_order":{{i}}}""");

        return store;
    }

    /// <summary>A window started from the settings file, shown off the screen, on the project.</summary>
    private MainForm Shown()
    {
        var window = TestWindow.Start(_config, Store(), new FakeApi(), out var presenter);
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-32000, -32000);
        window.ShowInTaskbar = false;
        window.Show();

        presenter.Select(ViewSelection.OfProject("p"));
        return window;
    }

    private static int[] Widths(MainForm window)
        => [.. TestWindow.Find<OutlineView>(window).Columns.Cast<ColumnHeader>().Select(c => c.Width)];

    [WinFormsFact]
    public void A_window_with_nothing_saved_opens_at_the_default_for_its_screen()
    {
        using var window = Shown();

        var working = Screen.FromPoint(Control.MousePosition).WorkingArea;
        var (width, height) = StartupLayout.Window(window.DeviceDpi, working.Width, working.Height);

        Assert.Equal(
            new Size(Math.Max(width, window.MinimumSize.Width), Math.Max(height, window.MinimumSize.Height)),
            window.Size);
    }

    [WinFormsTheory]
    [InlineData(1000, 650)]
    [InlineData(1100, 700)]
    [InlineData(1600, 900)]
    public void Every_column_shows_when_none_has_been_sized_yet(int width, int height)
    {
        File.WriteAllText(_config, $$$"""{"view":{"windowWidth":{{{width}}},"windowHeight":{{{height}}}}}""");

        using var window = Shown();
        var outline = TestWindow.Find<OutlineView>(window);
        var taken = Widths(window).Sum();

        Assert.True(
            taken <= outline.ClientSize.Width,
            $"the columns take {taken}px of an outline {outline.ClientSize.Width}px wide");
    }

    [WinFormsFact]
    public void A_window_too_narrow_for_them_all_still_leaves_the_task_room_to_read()
    {
        // Past this the other columns are what give way: a task's name is what the row is for.
        File.WriteAllText(_config, """{"view":{"windowWidth":700,"windowHeight":500}}""");

        using var window = Shown();
        var outline = TestWindow.Find<OutlineView>(window);

        Assert.Equal(StartupLayout.Scaled(StartupLayout.TaskColumnLeast, outline.DeviceDpi), Widths(window)[0]);
    }

    [WinFormsFact]
    public void Columns_sized_by_hand_come_back_after_a_restart()
    {
        File.WriteAllText(_config, """{"view":{"windowWidth":1100,"windowHeight":700}}""");

        int[] sized;
        using (var window = Shown())
        {
            var columns = TestWindow.Find<OutlineView>(window).Columns;
            columns[0].Width = 300;
            columns[2].Width = 175;
            sized = Widths(window);

            window.Close();
        }

        using var second = Shown();

        Assert.Equal(sized, Widths(second));
    }

    [WinFormsFact]
    public void A_window_sized_by_hand_comes_back_that_size()
    {
        File.WriteAllText(_config, """{"view":{"windowWidth":1300,"windowHeight":800}}""");

        using var window = Shown();

        Assert.Equal(new Size(1300, 800), window.Size);
    }
}
