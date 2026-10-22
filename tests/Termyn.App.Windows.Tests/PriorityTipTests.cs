using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The tip that says what a row's priority dot means.
/// </summary>
/// <remarks>
/// Where it answers and what it's set to say, not whether it shows: the tip comes up on a timer
/// once the pointer has rested, and only for a window in front, which a test host's isn't.
/// </remarks>
public class PriorityTipTests
{
    private static TaskRow Task(string id, Priority priority)
        => new(id, $"Task {id}", priority, "Work", string.Empty, []);

    private static TaskRow Heading(string said)
        => new($"day:{said}", said, Priority.P4, string.Empty, string.Empty, [], IsHeading: true);

    private static Form Window()
        => new() { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(700, 400) };

    /// <summary>A list with a task at each priority, and a day's heading.</summary>
    private static OutlineView Outline(Form form)
    {
        var outline = new OutlineView { Dock = DockStyle.Fill };
        form.Controls.Add(outline);
        form.Show();

        outline.Rows =
        [
            Task("a", Priority.P1),
            Task("b", Priority.P2),
            Task("c", Priority.P3),
            Task("d", Priority.P4),
            Heading("1 Aug · Tomorrow"),
        ];

        return outline;
    }

    /// <summary>
    /// The middle of a row's priority cell, found from the columns rather than by counting pixels.
    /// </summary>
    private static Point PriorityCellOf(OutlineView outline, int index)
    {
        var row = outline.GetItemRect(index);
        var left = row.Left;

        foreach (ColumnHeader header in outline.Columns)
        {
            if (header.Tag is TaskColumn.Priority)
                return new Point(left + (header.Width / 2), row.Top + (row.Height / 2));

            left += header.Width;
        }

        throw new InvalidOperationException("the outline has no priority column");
    }

    [WinFormsFact]
    public void Each_dot_says_its_priority()
    {
        using var form = Window();
        using var outline = Outline(form);

        Assert.Equal("Priority 1 — urgent", outline.PriorityTipAt(PriorityCellOf(outline, 0)));
        Assert.Equal("Priority 2 — high", outline.PriorityTipAt(PriorityCellOf(outline, 1)));
        Assert.Equal("Priority 3 — medium", outline.PriorityTipAt(PriorityCellOf(outline, 2)));
    }

    [WinFormsFact]
    public void Nothing_is_said_where_no_dot_is_drawn()
    {
        // A task with no priority gets no dot, and a tip over an empty cell would be describing
        // something nobody can see. A heading has no priority to describe.
        using var form = Window();
        using var outline = Outline(form);

        Assert.Null(outline.PriorityTipAt(PriorityCellOf(outline, 3)));
        Assert.Null(outline.PriorityTipAt(PriorityCellOf(outline, 4)));
    }

    [WinFormsFact]
    public void Only_the_priority_column_answers()
    {
        // The task's name, a little way into its row, isn't the dot.
        using var form = Window();
        using var outline = Outline(form);

        var row = outline.GetItemRect(0);

        Assert.Null(outline.PriorityTipAt(new Point(row.Left + 60, row.Top + (row.Height / 2))));
    }

    [WinFormsFact]
    public void The_whole_cell_answers_not_just_the_dot()
    {
        // The dot's nine pixels across. A tip that only came up on those would be one people
        // didn't know was there.
        using var form = Window();
        using var outline = Outline(form);

        var middle = PriorityCellOf(outline, 0);
        var across = 0;

        for (var x = middle.X - 30; x < middle.X + 30; x++)
            if (outline.PriorityTipAt(new Point(x, middle.Y)) is not null)
                across++;

        Assert.True(across >= 16, $"the tip answers across {across}px");
    }

    [WinFormsFact]
    public void Moving_onto_a_dot_sets_the_tip_and_moving_off_takes_it_away()
    {
        using var form = Window();
        using var outline = Outline(form);

        RealMouse.Move(outline, PriorityCellOf(outline, 0));
        Assert.Equal("Priority 1 — urgent", outline.ShownTip);

        RealMouse.Move(outline, PriorityCellOf(outline, 1));
        Assert.Equal("Priority 2 — high", outline.ShownTip);

        RealMouse.Move(outline, PriorityCellOf(outline, 3));
        Assert.Equal(string.Empty, outline.ShownTip);
    }
}
