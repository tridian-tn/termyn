using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// A day's heading in the outline: a row the list holds like any other, which nothing may treat
/// as a task.
/// </summary>
/// <remarks>
/// The selection is where this goes wrong. A heading sits between the tasks, so every arrow key
/// and every click can land on one, and a command that took it would act on a row with no task
/// behind it.
/// </remarks>
public class HeadingRowTests
{
    /// <summary>Two days, each headed, with two tasks under the first and one under the second.</summary>
    private static OutlineView Outline()
    {
        var outline = new OutlineView();
        outline.CreateControl();

        outline.Rows =
        [
            Heading("1 Aug · Tomorrow"),
            Task("a", "First"),
            Task("b", "Second"),
            Heading("2 Aug · Sunday"),
            Task("c", "Third"),
        ];

        return outline;
    }

    private static TaskRow Heading(string said)
        => new($"day:{said}", said, Priority.P4, string.Empty, string.Empty, [], IsHeading: true);

    private static TaskRow Task(string id, string content)
        => new(id, content, Priority.P4, "Work", string.Empty, []);

    private static void Select(OutlineView outline, int index)
    {
        outline.SelectedIndices.Clear();
        outline.SelectedIndices.Add(index);
    }

    [WinFormsFact]
    public void Arriving_on_a_heading_from_above_carries_on_down()
    {
        using var outline = Outline();

        Select(outline, 2);   // "Second", the row above the second heading
        Select(outline, 3);   // the heading itself, as ↓ would reach it

        Assert.Equal("Third", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void Arriving_on_a_heading_from_below_carries_on_up()
    {
        using var outline = Outline();

        Select(outline, 4);   // "Third"
        Select(outline, 3);   // the heading above it, as ↑ would reach it

        Assert.Equal("Second", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void The_heading_at_the_top_hands_the_selection_down_whichever_way_it_came()
    {
        // Nothing above it to carry on to, and the list starts on one in Upcoming — so arriving
        // there from below has to turn round rather than leave the selection on a heading.
        using var outline = Outline();

        Select(outline, 1);
        Select(outline, 0);

        Assert.Equal("First", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void A_heading_is_never_what_the_window_is_told_is_selected()
    {
        using var outline = Outline();

        Select(outline, 3);

        Assert.NotNull(outline.SelectedRow);
        Assert.False(outline.SelectedRow?.IsHeading);
        Assert.NotEqual("day:2 Aug · Sunday", outline.SelectedId);
    }

    [WinFormsFact]
    public void Stepping_past_a_heading_is_no_noisier_than_an_ordinary_move()
    {
        // The step means clearing an index and adding another, and each of those is an event of
        // its own. Held quiet, so landing on a heading says no more about the selection than
        // landing on the task below it would.
        using var outline = Outline();

        Assert.Equal(Moves(outline, from: 1, to: 2), Moves(outline, from: 2, to: 3));
        Assert.Equal("Third", outline.SelectedRow?.Content);

        static int Moves(OutlineView outline, int from, int to)
        {
            Select(outline, from);

            var said = 0;
            void Count(object? sender, EventArgs e) => said++;

            outline.SelectedIndexChanged += Count;
            Select(outline, to);
            outline.SelectedIndexChanged -= Count;

            return said;
        }
    }

    [WinFormsFact]
    public void Which_way_the_selection_was_going_is_forgotten_with_the_rows()
    {
        // A sync replaces these every 45 seconds. An index left over from the last lot names a
        // different task, and the step off a heading would be sent the wrong way by it.
        using var outline = Outline();

        Select(outline, 4);   // travelling up, as far as the old rows were concerned
        outline.Rows = outline.Rows.ToList();

        Select(outline, 3);

        Assert.Equal("Third", outline.SelectedRow?.Content);
    }

    [WinFormsFact]
    public void The_list_knows_a_heading_by_where_it_is_on_screen()
    {
        // What a click is turned away by. Dropping the click where it lands is the whole of the
        // fix for the flash: letting the list select a heading and moving the selection off again
        // lights up the row next to it and puts it out, on a row nobody clicked.
        using var outline = Outline();
        outline.Size = new Size(400, 300);

        var heading = outline.GetItemRect(3);
        var task = outline.GetItemRect(4);

        Assert.True(outline.IsHeadingAt(new Point(heading.Left + 20, heading.Top + (heading.Height / 2))));
        Assert.False(outline.IsHeadingAt(new Point(task.Left + 20, task.Top + (task.Height / 2))));
    }

    [WinFormsFact]
    public void A_heading_hands_over_its_day_and_nothing_else()
    {
        // What the control is given as the row's cells, which is what a screen reader reads out.
        using var outline = Outline();

        Assert.Equal(
            ["1 Aug · Tomorrow", "", "", "", "", ""],
            outline.Cells(outline.Rows[0]));
    }
}
