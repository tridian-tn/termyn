using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The box on a row that ticks a task off.
/// </summary>
/// <remarks>
/// Where it is and what it asks for, rather than how it looks: a virtual owner-drawn list won't
/// render its rows into a bitmap, so the drawing is two calls and the eye. What can be held here is
/// that a click lands on the right task, that it leaves the selection alone, and that the box is
/// somewhere a person can hit.
/// </remarks>
public class CheckboxTests
{
    private static TaskRow Task(string id, string content, int depth = 0, bool completed = false)
        => new(id, content, Priority.P4, "Work", string.Empty, [], Depth: depth, Completed: completed);

    private static TaskRow Heading(string said)
        => new($"day:{said}", said, Priority.P4, string.Empty, string.Empty, [], IsHeading: true);

    /// <summary>A list with a parent, its sub-task, a finished task, and a day's heading.</summary>
    private static OutlineView Outline(Form form)
    {
        var outline = new OutlineView { Dock = DockStyle.Fill };
        form.Controls.Add(outline);
        form.Show();

        outline.Rows =
        [
            Task("a", "First"),
            Task("b", "Under it", depth: 1),
            Task("c", "Done with", completed: true),
            Heading("1 Aug · Tomorrow"),
            Task("d", "Tomorrow's"),
        ];

        return outline;
    }

    private static Form Window()
        => new() { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(700, 400) };

    /// <summary>
    /// Where a row's box is, found by asking the list rather than by counting pixels.
    /// </summary>
    /// <param name="outline">The list to look in</param>
    /// <param name="index">The row to find the box on</param>
    /// <returns>A point inside that row's box</returns>
    private static Point? BoxOf(OutlineView outline, int index)
    {
        var row = outline.GetItemRect(index);
        var id = outline.Rows[index].Id;

        for (var x = row.Left; x < row.Left + 200; x++)
        {
            var at = new Point(x, row.Top + (row.Height / 2));

            if (outline.CheckboxAt(at) == id)
                return at;
        }

        return null;
    }

    private static void Click(OutlineView outline, Point at)
    {
        outline.PressAt(MouseButtons.Left, at);
        Application.DoEvents();
    }

    [WinFormsFact]
    public void Every_task_has_a_box_and_a_day_has_none()
    {
        using var form = Window();
        using var outline = Outline(form);

        Assert.NotNull(BoxOf(outline, 0));
        Assert.NotNull(BoxOf(outline, 2));   // the finished one, which is how it gets put back
        Assert.Null(BoxOf(outline, 3));      // the heading, which is no task
    }

    [WinFormsFact]
    public void A_sub_tasks_box_sits_in_from_its_parents()
    {
        // It's drawn inside the task's own column, past the indent, so it steps in with the row
        // rather than standing in a column of its own away from the words it belongs to.
        using var form = Window();
        using var outline = Outline(form);

        var parent = BoxOf(outline, 0);
        var child = BoxOf(outline, 1);

        Assert.NotNull(parent);
        Assert.NotNull(child);
        Assert.True(child!.Value.X > parent!.Value.X, $"child's box at {child.Value.X}, parent's at {parent.Value.X}");
    }

    [WinFormsFact]
    public void The_box_is_big_enough_to_hit()
    {
        // The room around the square answers as well as the square: a thirteen-pixel target is one
        // people miss, and a box you have to aim at is worse than no box.
        using var form = Window();
        using var outline = Outline(form);

        var row = outline.GetItemRect(0);
        var across = 0;

        for (var x = row.Left; x < row.Left + 200; x++)
            if (outline.CheckboxAt(new Point(x, row.Top + (row.Height / 2))) == "a")
                across++;

        Assert.True(across >= 16, $"the box answers across {across}px");
    }

    [WinFormsFact]
    public void Clicking_it_asks_for_that_task_and_leaves_the_selection_alone()
    {
        // Ticking a task off is not choosing it. Moving the selection would take the reader off
        // whatever they were looking at, which is what clicking an expander already avoids.
        using var form = Window();
        using var outline = Outline(form);

        outline.SelectId("d");

        var asked = new List<string>();
        outline.ToggleRequested += asked.Add;

        Click(outline, BoxOf(outline, 0)!.Value);

        Assert.Equal(["a"], asked);
        Assert.Equal("Tomorrow's", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void Clicking_the_words_asks_for_nothing()
    {
        using var form = Window();
        using var outline = Outline(form);

        var asked = new List<string>();
        outline.ToggleRequested += asked.Add;

        var row = outline.GetItemRect(0);
        Click(outline, new Point(row.Left + 150, row.Top + (row.Height / 2)));

        Assert.Empty(asked);
    }

    [WinFormsFact]
    public void A_finished_task_is_drawn_with_a_tick_in_it_and_an_unfinished_one_without()
    {
        // The one thing about the drawing that can be held: which of the two it drew. Rendered to
        // a bitmap of its own, which a pure draw will do even though the list itself won't.
        var box = OutlineView.Checkbox(new Rectangle(0, 0, 20, 18));

        Assert.True(Marks(box, ticked: true) > Marks(box, ticked: false));

        static int Marks(Rectangle box, bool ticked)
        {
            using var bitmap = new Bitmap(24, 20);
            using var g = Graphics.FromImage(bitmap);

            g.Clear(Color.White);
            OutlineView.DrawCheck(g, box, ticked, Color.Black);

            var marked = 0;

            for (var x = 0; x < bitmap.Width; x++)
                for (var y = 0; y < bitmap.Height; y++)
                    if (bitmap.GetPixel(x, y).R < 200)
                        marked++;

            return marked;
        }
    }
}
