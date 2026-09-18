using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Which colour the outline writes a row's project and labels in.
/// </summary>
/// <remarks>
/// The decisions are asserted rather than the pixels. A list in virtual owner-draw mode won't render
/// its rows into a bitmap — <c>DrawToBitmap</c> came back with the column header and nothing else —
/// so a screenshot test here would have passed on the header alone and said nothing about the rows.
/// What it can't cover is the drawing itself, which is two calls and the eye.
/// </remarks>
public class OutlineColourTests
{
    private static readonly Rgb BerryRed = TodoistPalette.Of("berry_red");

    private static readonly Color Muted = Color.FromArgb(0x6B, 0x70, 0x79);

    private static TaskRow Row(IReadOnlyList<string>? labels = null, Rgb? colour = null, string project = "Work")
        => new("t1", "Plan the week", Priority.P4, project, string.Empty, labels ?? [], ProjectColour: colour);

    private static OutlineView Outline(params (string Label, Rgb Colour)[] labels)
    {
        var outline = new OutlineView
        {
            LabelColours = labels.ToDictionary(l => l.Label, l => Theme.ToColor(l.Colour), StringComparer.Ordinal),
        };

        return outline;
    }

    // ---- The project's dot -----------------------------------------------------------------------

    [WinFormsFact]
    public void A_project_is_marked_with_the_colour_todoist_gives_it()
        => Assert.Equal(Theme.ToColor(BerryRed), OutlineView.ProjectDot(Row(colour: BerryRed), selected: false));

    [WinFormsFact]
    public void A_task_in_no_project_has_no_dot()
    {
        // As the presenter builds it: no project found, so no colour, so nothing to draw. The row
        // can't hold a colour without a project, which is why that's the only case to answer.
        Assert.Null(OutlineView.ProjectDot(Row(project: string.Empty), selected: false));
    }

    [WinFormsFact]
    public void A_selected_row_keeps_the_accent_it_is_drawn_in()
    {
        // The accent is behind the row, and a colour picked to read against the panel has made no
        // promise about reading against that.
        Assert.Null(OutlineView.ProjectDot(Row(colour: BerryRed), selected: true));
    }

    // ---- The labels ------------------------------------------------------------------------------

    [WinFormsFact]
    public void Each_label_is_written_in_its_own_colour()
    {
        // One colour for the lot is the easy mistake, and it would look right on a task with one
        // label.
        using var outline = Outline(("followup", TodoistPalette.Of("teal")), ("waiting", TodoistPalette.Of("grape")));

        var runs = outline.LabelRuns(Row(["followup", "waiting"]), selected: false, Muted);

        Assert.Equal(
            [("@followup", Theme.ToColor(TodoistPalette.Of("teal"))), ("@waiting", Theme.ToColor(TodoistPalette.Of("grape")))],
            runs);
    }

    [WinFormsFact]
    public void A_label_the_window_has_not_been_told_about_still_reads()
    {
        // The account can hold a label this window hasn't seen described yet — one just made, before
        // the sync that carries it. It reads as every label did before there were colours.
        using var outline = Outline();

        var runs = outline.LabelRuns(Row(["followup"]), selected: false, Muted);

        Assert.Equal([("@followup", Muted)], runs);
    }

    [WinFormsFact]
    public void A_selected_rows_labels_are_one_run_in_one_colour()
    {
        using var outline = Outline(("followup", TodoistPalette.Of("teal")), ("waiting", TodoistPalette.Of("grape")));

        var runs = outline.LabelRuns(Row(["followup", "waiting"]), selected: true, Muted);

        Assert.Equal([("@followup @waiting", Muted)], runs);
    }

    [WinFormsFact]
    public void A_row_with_no_labels_writes_nothing()
    {
        using var outline = Outline();

        Assert.Empty(outline.LabelRuns(Row(), selected: false, Muted));
        Assert.Empty(outline.LabelRuns(Row(), selected: true, Muted));
    }
}
