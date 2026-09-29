using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Where a selection goes when it lands on a day's heading, which nothing may treat as a task.
/// </summary>
/// <remarks>
/// That the outline asks this when its selection lands on a heading, and publishes only where the
/// selection ends up, is the control's half, and tested with it in <c>HeadingRowTests</c>.
/// </remarks>
public class OutlineSelectionTests
{
    /// <summary>Two days, each headed, with two tasks under the first and two under the second.</summary>
    private static readonly IReadOnlyList<TaskRow> Rows =
    [
        Heading("1 Aug · Tomorrow"),
        Task("a", "First"),
        Task("b", "Second"),
        Heading("2 Aug · Sunday"),
        Task("c", "Third"),
        Task("d", "Fourth"),
    ];

    private static TaskRow Heading(string said)
        => new($"day:{said}", said, Priority.P4, string.Empty, string.Empty, [], IsHeading: true);

    private static TaskRow Task(string id, string content)
        => new(id, content, Priority.P4, "Work", string.Empty, []);

    [Fact]
    public void Arriving_on_a_heading_from_above_carries_on_down()
    {
        // From "Second", the row above the second heading, as ↓ would reach it.
        Assert.Equal(4, OutlineSelection.PastHeading(Rows, index: 3, lastOnTask: 2));
    }

    [Fact]
    public void Arriving_on_a_heading_from_below_carries_on_up()
    {
        // From "Third", the row below it, as ↑ would reach it.
        Assert.Equal(2, OutlineSelection.PastHeading(Rows, index: 3, lastOnTask: 4));
    }

    [Fact]
    public void The_heading_at_the_top_hands_the_selection_down_whichever_way_it_came()
    {
        // Nothing above it to carry on to, and the list starts on one in Upcoming — so arriving
        // there from below has to turn round rather than leave the selection on a heading.
        Assert.Equal(1, OutlineSelection.PastHeading(Rows, index: 0, lastOnTask: 1));
    }

    [Fact]
    public void A_heading_at_the_bottom_hands_the_selection_back_up()
    {
        // The other end, where carrying on down would leave the list.
        IReadOnlyList<TaskRow> rows = [.. Rows, Heading("3 Aug · Monday")];

        Assert.Equal(5, OutlineSelection.PastHeading(rows, index: 6, lastOnTask: 5));
    }

    [Fact]
    public void Nothing_to_land_on_either_way_is_said_rather_than_guessed()
    {
        IReadOnlyList<TaskRow> rows = [Heading("1 Aug · Tomorrow"), Heading("2 Aug · Sunday")];

        Assert.Null(OutlineSelection.PastHeading(rows, index: 1, lastOnTask: -1));
    }

    [Fact]
    public void A_selection_that_hasnt_been_on_a_task_yet_goes_down()
    {
        // A click on a heading before anything was selected takes the day it heads.
        Assert.Equal(4, OutlineSelection.PastHeading(Rows, index: 3, lastOnTask: -1));
    }
}
