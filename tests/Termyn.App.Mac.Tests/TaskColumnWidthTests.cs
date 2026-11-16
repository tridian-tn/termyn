using System.Globalization;
using Termyn.Presentation;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// How wide the table's date columns start in a region whose dates are longer than the Windows
/// outline's widths allow.
/// </summary>
public class TaskColumnWidthTests
{
    /// <summary>The room the table leaves between columns, made up rather than the table's own.</summary>
    private const double Spacing = 17;

    /// <summary>The space a cell leaves either side of its words, four points a side.</summary>
    private const double CellInsets = 8;

    [AppKitFact]
    public void The_deadline_column_starts_wide_enough_for_a_long_date_in_another_year()
    {
        var spanish = CultureInfo.GetCultureInfo("es-ES");

        Fits(TaskColumn.Deadline, spanish, DisplayDates.DayMonthYear(new DateOnly(2027, 9, 28), spanish));
    }

    [AppKitFact]
    public void The_due_column_starts_wide_enough_for_a_time_written_with_words()
    {
        var korean = CultureInfo.GetCultureInfo("ko-KR");

        Fits(TaskColumn.Due, korean, DisplayDates.DayMonthAndTime(new DateTime(2026, 12, 28, 22, 30, 0), korean));
    }

    [AppKitFact]
    public void A_region_whose_dates_fit_keeps_the_usual_widths()
    {
        // Numbers only, so nothing it writes comes near either column's width.
        var numeric = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        numeric.DateTimeFormat.LongDatePattern = "d/M/yy";
        numeric.DateTimeFormat.MonthDayPattern = "d/M";
        numeric.DateTimeFormat.ShortTimePattern = "H:mm";

        Assert.Equal((double)StartupLayout.ColumnWidth(TaskColumn.Due), (double)TaskListController.StartWidth(TaskColumn.Due, numeric, Spacing));
        Assert.Equal((double)StartupLayout.ColumnWidth(TaskColumn.Deadline), (double)TaskListController.StartWidth(TaskColumn.Deadline, numeric, Spacing));
    }

    private static void Fits(TaskColumn column, CultureInfo culture, string date)
    {
        var room = (double)TaskListController.StartWidth(column, culture, Spacing) - Spacing - CellInsets;

        using var label = NSTextField.CreateLabel(date);
        label.Font = NSFont.GetPreferredFont(NSFontTextStyle.Body.GetConstant()!, new NSDictionary());
        double needs = label.FittingSize.Width;

        Assert.True(needs <= room, $"“{date}” needs {needs}pt and the {column} column has {room}pt");
    }
}
