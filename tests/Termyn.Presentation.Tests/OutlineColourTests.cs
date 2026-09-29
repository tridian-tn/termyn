using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Which colour the outline writes a row's project and labels in.
/// </summary>
/// <remarks>
/// The decisions are asserted rather than the pixels. A list in virtual owner-draw mode won't render
/// its rows into a bitmap — <c>DrawToBitmap</c> came back with the column header and nothing else —
/// so a screenshot test would have passed on the header alone and said nothing about the rows.
/// What it can't cover is the drawing itself, which is two calls and the eye.
/// </remarks>
public class OutlineColourTests
{
    private static readonly Rgb BerryRed = TodoistPalette.Of("berry_red");
    private static readonly Rgb Teal = TodoistPalette.Of("teal");
    private static readonly Rgb Grape = TodoistPalette.Of("grape");

    private static TaskRow Row(IReadOnlyList<string>? labels = null, Rgb? colour = null, string project = "Work")
        => new("t1", "Plan the week", Priority.P4, project, string.Empty, labels ?? [], ProjectColour: colour);

    private static Dictionary<string, Rgb> Colours(params (string Label, Rgb Colour)[] labels)
        => labels.ToDictionary(l => l.Label, l => l.Colour, StringComparer.Ordinal);

    // ---- The project's dot -----------------------------------------------------------------------

    [Fact]
    public void A_project_is_marked_with_the_colour_todoist_gives_it()
        => Assert.Equal(BerryRed, OutlineCells.ProjectDot(Row(colour: BerryRed), selected: false));

    [Fact]
    public void A_task_in_no_project_has_no_dot()
    {
        // As the presenter builds it: no project found, so no colour, so nothing to draw. The row
        // can't hold a colour without a project, which is why that's the only case to answer.
        Assert.Null(OutlineCells.ProjectDot(Row(project: string.Empty), selected: false));
    }

    [Fact]
    public void A_selected_row_keeps_the_accent_it_is_drawn_in()
    {
        // The accent is behind the row, and a colour picked to read against the panel has made no
        // promise about reading against that.
        Assert.Null(OutlineCells.ProjectDot(Row(colour: BerryRed), selected: true));
    }

    // ---- The labels ------------------------------------------------------------------------------

    [Fact]
    public void Each_label_is_written_in_its_own_colour()
    {
        // One colour for the lot is the easy mistake, and it would look right on a task with one
        // label.
        var runs = OutlineCells.LabelRuns(Row(["followup", "waiting"]), selected: false, Colours(("followup", Teal), ("waiting", Grape)));

        Assert.Equal([("@followup", (Rgb?)Teal), ("@waiting", Grape)], runs);
    }

    [Fact]
    public void A_label_the_window_has_not_been_told_about_still_reads()
    {
        // The account can hold a label this window hasn't seen described yet — one just made, before
        // the sync that carries it. It reads as every label did before there were colours: muted,
        // which is whatever colour the window draws quiet text in.
        var runs = OutlineCells.LabelRuns(Row(["followup"]), selected: false, Colours());

        Assert.Equal([("@followup", (Rgb?)null)], runs);
    }

    [Fact]
    public void A_selected_rows_labels_are_one_run_in_the_muted_colour()
    {
        var runs = OutlineCells.LabelRuns(Row(["followup", "waiting"]), selected: true, Colours(("followup", Teal), ("waiting", Grape)));

        Assert.Equal([("@followup @waiting", (Rgb?)null)], runs);
    }

    [Fact]
    public void A_row_with_no_labels_writes_nothing()
    {
        Assert.Empty(OutlineCells.LabelRuns(Row(), selected: false, Colours()));
        Assert.Empty(OutlineCells.LabelRuns(Row(), selected: true, Colours()));
    }
}
