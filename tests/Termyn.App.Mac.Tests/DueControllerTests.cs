using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Mac.Tests;

/// <summary>The due-date popover, with its view loaded but not shown.</summary>
public class DueControllerTests
{
    [AppKitFact]
    public async Task A_day_clicked_lands_on_a_task_the_server_has_just_renamed()
    {
        var api = new FakeApi();
        var presenter = FakeAccount.Presenter(api);
        var commands = FakeAccount.Commands(presenter);
        var temporary = presenter.AddSubtask("a", "New")!;
        using var controller = new DueController(presenter, commands, temporary, () => { });
        _ = controller.View;

        api.Next = FakeApi.Naming(new Dictionary<string, string> { ["item_add"] = "real1" });
        await presenter.SyncAsync(TestContext.Current.CancellationToken);

        var calendar = controller.View.Subviews.OfType<NSDatePicker>().Single();
        calendar.DateValue = new NSCalendar(NSCalendarType.Gregorian).DateFromComponents(new NSDateComponents { Year = 2026, Month = 9, Day = 1, Hour = 12 })!;
        NSApplication.SharedApplication.SendAction(calendar.Action!, calendar.Target, calendar);

        Assert.Equal(new DateOnly(2026, 9, 1), commands.Row("real1")!.DueOn);
    }
}
