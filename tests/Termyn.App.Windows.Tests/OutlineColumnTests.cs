using System.Globalization;
using Termyn.Core.Model;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Which columns the outline stands, and the cells it hands the control for them.
/// </summary>
/// <remarks>
/// The words rather than the pixels, for the reason <c>OutlineColourTests</c> gives: a list
/// in virtual owner-draw mode won't render its rows into a bitmap. What these do cover is the part
/// that went wrong when a column was added — a cell drawn under the heading next to its own. What
/// each cell says, and how it's filled, is Presentation's, and tested there in
/// <c>OutlineCellsTests</c>.
/// </remarks>
public class OutlineColumnTests
{
    private static TaskRow Row() => new(
        "t1",
        "Plan the week",
        Priority.P2,
        "Work",
        "1 Aug",
        ["followup"],
        Deadline: "4 Aug");

    [WinFormsFact]
    public void The_deadline_stands_in_a_column_of_its_own_beside_the_due_date()
    {
        using var outline = new OutlineView();

        Assert.Equal(
            [
                TaskColumn.Content,
                TaskColumn.Priority,
                TaskColumn.Project,
                TaskColumn.Due,
                TaskColumn.Deadline,
                TaskColumn.Labels,
            ],
            outline.Columns.Cast<ColumnHeader>().Select(c => c.Tag).ToArray());

        // And headed with what it holds, since the column is the only thing saying which date a
        // row's two dates is which.
        Assert.Equal(["Task", "!", "Project", "Due", "Deadline", "Labels"], outline.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToArray());
    }

    [WinFormsFact]
    public void A_row_is_handed_over_one_cell_per_column_in_the_order_they_stand()
    {
        // The control is given these as the row's sub-items, which is what a screen reader reads
        // and what an exported row would say. A column added without a cell shows up here as a gap.
        using var outline = new OutlineView();

        Assert.Equal(["Plan the week", string.Empty, "Work", "1 Aug", "4 Aug", "@followup"], outline.Cells(Row()));
    }

    [WinFormsFact]
    public void A_task_with_no_deadline_hands_over_an_empty_cell()
    {
        // The whole row rather than the one cell: read by position, this would go on passing if a
        // column were inserted in front of it and the empty cell being read were some other one's.
        using var outline = new OutlineView();

        Assert.Equal(
            ["Plan the week", string.Empty, "Work", "1 Aug", string.Empty, "@followup"],
            outline.Cells(Row() with { Deadline = string.Empty }));
    }

    [WinFormsFact]
    public void Labels_are_written_in_the_colours_the_list_was_given()
    {
        // Which colour each label gets is Presentation's and tested there. This is the list's
        // half: that it writes in the colours handed to it, and not, say, in none at all — which
        // would look like every label being muted, and fail nothing else.
        var teal = Color.FromArgb(0x15, 0x8F, 0xAD);
        var muted = Color.FromArgb(0x6B, 0x70, 0x79);
        using var outline = new OutlineView
        {
            LabelColours = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase) { ["followup"] = teal },
        };

        var runs = outline.LabelRuns(Row() with { Labels = ["followup", "waiting"] }, selected: false, muted);

        Assert.Equal([("@followup", teal), ("@waiting", muted)], runs);
    }

    // ---- Widths ----------------------------------------------------------------------------------

    [WinFormsFact]
    public void The_deadline_column_starts_wide_enough_for_a_long_date_in_another_year()
    {
        // Spanish writes one as "28 de sept de 2027", which needs far more room than an English
        // date. September's is the longest of its months.
        using var spanish = new CultureScope("es-ES");
        using var outline = new OutlineView();

        outline.LayColumns(new Dictionary<TaskColumn, int>());

        Fits(outline, TaskColumn.Deadline, DisplayDates.DayMonthYear(new DateOnly(2027, 9, 28), CultureInfo.CurrentCulture));
    }

    [WinFormsFact]
    public void The_due_column_starts_wide_enough_for_a_time_written_with_words()
    {
        // Korean puts a word for the half of the day before the time: "12월 28일 오후 10:30".
        using var korean = new CultureScope("ko-KR");
        using var outline = new OutlineView();

        outline.LayColumns(new Dictionary<TaskColumn, int>());

        Fits(outline, TaskColumn.Due, DisplayDates.DayMonthAndTime(new DateTime(2026, 12, 28, 22, 30, 0), CultureInfo.CurrentCulture));
    }

    [WinFormsFact]
    public void A_region_whose_dates_fit_keeps_the_usual_widths()
    {
        using var british = new CultureScope(Regions.British);
        using var outline = new OutlineView();

        outline.LayColumns(new Dictionary<TaskColumn, int>());

        Assert.Equal(StartupLayout.Scaled(StartupLayout.ColumnWidth(TaskColumn.Due), outline.DeviceDpi), outline.ColumnWidths[TaskColumn.Due]);
        Assert.Equal(StartupLayout.Scaled(StartupLayout.ColumnWidth(TaskColumn.Deadline), outline.DeviceDpi), outline.ColumnWidths[TaskColumn.Deadline]);
    }

    [WinFormsFact]
    public void A_width_the_user_saved_is_kept_however_long_the_regions_dates_are()
    {
        using var spanish = new CultureScope("es-ES");
        using var outline = new OutlineView();

        outline.LayColumns(new Dictionary<TaskColumn, int> { [TaskColumn.Deadline] = 60 });

        Assert.Equal(60, outline.ColumnWidths[TaskColumn.Deadline]);
    }

    /// <summary>The space a cell leaves either side of its text, six pixels a side.</summary>
    private const int CellInsets = 12;

    private static void Fits(OutlineView outline, TaskColumn column, string date)
    {
        var room = outline.ColumnWidths[column] - CellInsets;
        var needs = TextRenderer.MeasureText(date, outline.Font).Width;

        Assert.True(needs <= room, $"“{date}” needs {needs}px and the {column} column has {room}px");
    }
}
