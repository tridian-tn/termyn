namespace Termyn.Presentation.Tests;

/// <summary>
/// The sizes a window starts at, at whatever scale its screen runs at.
/// </summary>
/// <remarks>
/// Checked here because a test can't choose the scale of the screen it runs on, and the window's
/// own tests only ever see the one they're given.
/// </remarks>
public class StartupLayoutTests
{
    [Theory]
    [InlineData(96, 360)]
    [InlineData(120, 450)]
    [InlineData(144, 540)]
    [InlineData(192, 720)]
    public void A_size_grows_with_the_scale_of_the_screen(int dpi, int expected)
        => Assert.Equal(expected, StartupLayout.Scaled(360, dpi));

    [Fact]
    public void A_new_window_opens_at_the_default_for_its_scale()
    {
        Assert.Equal((1100, 700), StartupLayout.Window(96, 1920, 1040));

        // 125%, as on a 2560 by 1440 screen.
        Assert.Equal((1375, 875), StartupLayout.Window(120, 2560, 1400));
    }

    [Fact]
    public void A_new_window_leaves_some_of_a_small_screen_showing()
        => Assert.Equal((921, 655), StartupLayout.Window(96, 1024, 728));

    [Fact]
    public void The_task_column_takes_what_the_others_leave()
        => Assert.Equal(350, StartupLayout.TaskColumnWidth(room: 840, others: 490, dpi: 96));

    [Theory]
    [InlineData(96, 150)]
    [InlineData(144, 225)]
    public void The_task_column_is_never_made_narrower_than_its_floor(int dpi, int floor)
        => Assert.Equal(floor, StartupLayout.TaskColumnWidth(room: 500, others: 490, dpi: dpi));

    [Fact]
    public void Every_column_has_a_width_and_none_is_wider_than_the_task()
    {
        // The window's own tests check that they fit, by measuring the outline they're given. This
        // keeps the task's name, which is the point of a row, wider than anything beside it.
        var columns = Enum.GetValues<TaskColumn>().Where(c => c != TaskColumn.None).ToList();

        Assert.All(columns, c => Assert.True(StartupLayout.ColumnWidth(c) > 0, $"{c} has no width"));
        Assert.All(
            columns.Where(c => c != TaskColumn.Content),
            c => Assert.True(StartupLayout.ColumnWidth(c) < StartupLayout.ColumnWidth(TaskColumn.Content)));
    }
}
