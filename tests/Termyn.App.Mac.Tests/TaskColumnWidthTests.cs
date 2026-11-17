using System.Globalization;
using CoreGraphics;
using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// How wide the table's date columns start in a region whose dates are longer than the usual
/// widths allow. In the real table, with a real cell at the width it'd be given.
/// </summary>
/// <remarks>
/// AppKit keeps the table's widths in the defaults under its autosave name, and puts them back over
/// the ones it starts with, so they're cleared first. Left from an earlier run, they'd be what's
/// measured.
/// </remarks>
public class TaskColumnWidthTests
{
    [AppKitFact]
    public void The_deadline_column_starts_wide_enough_for_a_long_date_in_another_year()
    {
        // Spanish writes one as "28 de sept de 2027". September's is the longest of its months.
        using var spanish = new CultureScope("es-ES");
        var grid = Grid();

        var deadline = DisplayDates.DayMonthYear(new DateOnly(2027, 9, 28), CultureInfo.CurrentCulture);

        Fits(grid, TaskColumn.Deadline, Row() with { Deadline = deadline });
    }

    [AppKitFact]
    public void The_due_column_starts_wide_enough_for_a_time_with_a_reminder()
    {
        // Korean puts a word for the half of the day before the time, and the reminder's mark goes
        // in front of the lot: "⏰ 12월 28일 오후 10:30".
        using var korean = new CultureScope("ko-KR");
        var grid = Grid();

        var due = DisplayDates.DayMonthAndTime(new DateTime(2026, 12, 28, 22, 30, 0), CultureInfo.CurrentCulture);

        Fits(grid, TaskColumn.Due, Row() with { Due = due, ReminderCount = 1 });
    }

    [AppKitFact]
    public void A_region_whose_deadlines_fit_keeps_the_usual_deadline_width()
    {
        // The deadline column only. A due date has the reminder's mark in front, and with the room
        // AppKit leaves between columns, even "⏰ 28/12 22:30" needs more than the due column's
        // usual width here.
        using var figures = new CultureScope(Regions.Figures);
        var grid = Grid();

        Assert.Equal((double)StartupLayout.ColumnWidth(TaskColumn.Deadline), (double)Column(grid, TaskColumn.Deadline).Width);
    }

    /// <summary>The table of a task list loaded in the current culture, with no widths kept from before.</summary>
    private static NSTableView Grid()
    {
        var defaults = NSUserDefaults.StandardUserDefaults;
        foreach (var key in defaults.ToDictionary().Keys.Select(k => k.ToString()))
        {
            if (key.StartsWith("NSTableView", StringComparison.Ordinal) && key.EndsWith("TaskColumns", StringComparison.Ordinal))
            {
                defaults.RemoveObject(key);
            }
        }

        var controller = new TaskListController(FakeAccount.Commands(FakeAccount.Presenter()));
        return Descendants(controller.View).OfType<NSTableView>().Single(t => t.FindColumn(new NSString(nameof(TaskColumn.Deadline))) >= 0);
    }

    private static NSTableColumn Column(NSTableView grid, TaskColumn column)
        => grid.TableColumns().Single(c => c.Identifier == column.ToString());

    private static TaskRow Row() => new("a", "Ship it", Priority.P4, string.Empty, string.Empty, []);

    /// <summary>
    /// Lays a cell out at the width the table gives it in a column, and checks its words aren't
    /// squeezed narrower than they need.
    /// </summary>
    private static void Fits(NSTableView grid, TaskColumn column, TaskRow row)
    {
        var width = Column(grid, column).Width - grid.IntercellSpacing.Width;
        using var cell = new GridCell("GridCell") { Frame = new CGRect(0, 0, width, 24) };

        cell.Show(row, column, new Dictionary<string, Rgb>(), new DateOnly(2026, 7, 31));
        cell.LayoutSubtreeIfNeeded();

        var text = cell.TextField!;
        var needs = text.IntrinsicContentSize.Width;
        Assert.True(text.Frame.Width >= needs, $"“{text.StringValue}” needs {needs}pt and the {column} column's cell gives it {text.Frame.Width}pt");
    }

    private static IEnumerable<NSView> Descendants(NSView view)
        => view.Subviews.SelectMany(child => Descendants(child).Prepend(child));
}
