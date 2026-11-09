using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// The deadline popover, with its view loaded but not shown: what it opens on, what it takes and
/// refuses, and that closing on the day already set writes nothing.
/// </summary>
public class DeadlineControllerTests
{
    private readonly MainPresenter _presenter;
    private readonly TaskCommands _commands;
    private readonly List<string> _closes = [];

    public DeadlineControllerTests()
    {
        _presenter = FakeAccount.Presenter(prepare: store => store.PutResource(
            "items",
            "c",
            """{"id":"c","content":"Finish it","project_id":"p1","child_order":3,"deadline":{"date":"2026-08-10"}}"""));
        _commands = FakeAccount.Commands(_presenter);
    }

    private DateOnly? DeadlineOf(string id) => _commands.Row(id)!.DeadlineOn;

    private DeadlineController Open(string id) => new(_presenter, _commands, id, () => _closes.Add("closed"));

    [AppKitFact]
    public void It_opens_on_the_deadline_the_task_has()
    {
        using var controller = Open("c");

        Assert.Equal(new DateOnly(2026, 8, 10), Day(Calendar(controller)));
        Assert.NotEmpty(Field(controller).StringValue);
        Assert.True(Clear(controller).Enabled);
    }

    [AppKitFact]
    public void Without_a_deadline_it_opens_empty_on_today_with_Clear_greyed()
    {
        using var controller = Open("a");

        Assert.Equal(string.Empty, Field(controller).StringValue);
        Assert.Equal(_presenter.Today, Day(Calendar(controller)));
        Assert.False(Clear(controller).Enabled);
    }

    [AppKitFact]
    public void Return_sets_the_day_typed_and_closes()
    {
        using var controller = Open("a");

        Return(controller, "2026-08-20");

        Assert.Equal(new DateOnly(2026, 8, 20), DeadlineOf("a"));
        Assert.Single(_closes);
    }

    [AppKitFact]
    public void Return_on_an_empty_field_takes_the_deadline_off()
    {
        using var controller = Open("c");

        Return(controller, string.Empty);

        Assert.Null(DeadlineOf("c"));
        Assert.Single(_closes);
    }

    [AppKitFact]
    public void What_a_deadline_cant_be_is_refused_with_the_popover_left_open()
    {
        using var controller = Open("a");

        foreach (var typed in new[] { "2026-08-20 9am", "every monday", "whenever suits" })
        {
            Return(controller, typed);

            Assert.Null(DeadlineOf("a"));
            Assert.Empty(_closes);
            Assert.Empty(_presenter.History.Recent());
            Assert.Equal(NSColor.SystemRed, Verdict(controller).TextColor);
        }
    }

    [AppKitFact]
    public void The_line_under_the_field_follows_what_is_typed()
    {
        using var controller = Open("a");

        Type(controller, "every monday");
        Assert.Equal(DayBoxText.ForDeadline(_presenter.ReadDay("every monday"), _presenter.DatesInEnglish).Says, Verdict(controller).StringValue);

        Type(controller, string.Empty);
        Assert.Equal(DayBoxText.ForDeadline(_presenter.ReadDay(string.Empty), _presenter.DatesInEnglish).Says, Verdict(controller).StringValue);
        Assert.NotEqual(NSColor.SystemRed, Verdict(controller).TextColor);
    }

    [AppKitFact]
    public void Other_words_for_the_day_it_already_has_write_nothing()
    {
        using var controller = Open("c");

        Return(controller, "10 aug 2026");

        Assert.Equal(new DateOnly(2026, 8, 10), DeadlineOf("c"));
        Assert.Empty(_presenter.History.Recent());
        Assert.Single(_closes);
    }

    [AppKitFact]
    public void Clear_takes_the_deadline_off_and_closes()
    {
        using var controller = Open("c");

        var clear = Clear(controller);
        clear.PerformClick(clear);

        Assert.Null(DeadlineOf("c"));
        Assert.Single(_closes);
    }

    [AppKitFact]
    public void A_day_clicked_in_the_calendar_is_set_and_closes()
    {
        using var controller = Open("a");

        Pick(controller, new DateOnly(2026, 9, 1));

        Assert.Equal(new DateOnly(2026, 9, 1), DeadlineOf("a"));
        Assert.Single(_closes);
    }

    [AppKitFact]
    public void Clicking_the_day_it_already_has_writes_nothing()
    {
        using var controller = Open("c");

        Pick(controller, new DateOnly(2026, 8, 10));

        Assert.Empty(_presenter.History.Recent());
        Assert.Single(_closes);
    }

    [AppKitFact]
    public void A_task_gone_by_the_time_it_is_set_says_so_rather_than_closing_on_nothing()
    {
        var said = new List<string>();
        var commands = new TaskCommands(_presenter, new RecordingLog(), said.Add);
        using var controller = new DeadlineController(_presenter, commands, "a", () => _closes.Add("closed"));
        _ = controller.View;
        _presenter.Delete("a");
        var noted = _presenter.History.Recent().Count;

        Return(controller, "2026-08-20");

        Assert.Equal(["Deadline not changed — the task is finished or no longer there."], said);
        Assert.Equal(noted, _presenter.History.Recent().Count);
        Assert.Single(_closes);
    }

    [AppKitFact]
    public void What_it_shows_keeps_to_its_margins()
    {
        using var controller = Open("c");

        var view = controller.View;
        view.SetFrameSize(view.FittingSize);
        view.LayoutSubtreeIfNeeded();

        // By alignment rather than frame: a label's frame reaches a little past the text it lines up by.
        foreach (var shown in view.Subviews)
        {
            var edges = shown.GetAlignmentRectForFrame(shown.Frame);
            Assert.True(edges.X >= 14 && edges.GetMaxX() <= view.Frame.Width - 14, $"{shown.GetType().Name} at {edges} in {view.Frame}");
        }
    }

    [AppKitFact]
    public void Esc_leaves_the_task_as_it_was()
    {
        using var controller = Open("c");
        Type(controller, "2026-08-20");

        Press(controller, "cancelOperation:");

        Assert.Equal(new DateOnly(2026, 8, 10), DeadlineOf("c"));
        Assert.Single(_closes);
    }

    /// <summary>The line of text the user types into, among the labels that can't be.</summary>
    private static NSTextField Field(NSViewController controller)
        => controller.View.Subviews.OfType<NSTextField>().Single(f => f.Editable);

    private static NSTextField Verdict(NSViewController controller)
    {
        var views = controller.View.Subviews;
        return (NSTextField)views[Array.IndexOf(views, Field(controller)) + 1];
    }

    private static NSButton Clear(NSViewController controller)
        => controller.View.Subviews.OfType<NSButton>().Single();

    private static NSDatePicker Calendar(NSViewController controller)
        => controller.View.Subviews.OfType<NSDatePicker>().Single();

    private static DateOnly Day(NSDatePicker calendar)
    {
        var parts = new NSCalendar(NSCalendarType.Gregorian).Components(NSCalendarUnit.Year | NSCalendarUnit.Month | NSCalendarUnit.Day, calendar.DateValue);
        return new DateOnly((int)parts.Year, (int)parts.Month, (int)parts.Day);
    }

    /// <summary>Picks a day as a click in the calendar does: the value changes, then the action goes.</summary>
    private static void Pick(NSViewController controller, DateOnly day)
    {
        var calendar = Calendar(controller);
        calendar.DateValue = new NSCalendar(NSCalendarType.Gregorian).DateFromComponents(new NSDateComponents { Year = day.Year, Month = day.Month, Day = day.Day, Hour = 12 })!;
        NSApplication.SharedApplication.SendAction(calendar.Action!, calendar.Target, calendar);
    }

    private static void Type(NSViewController controller, string typed) => PopoverKeys.Type(Field(controller), typed);

    private static void Return(NSViewController controller, string typed) => PopoverKeys.Return(Field(controller), typed);

    private static void Press(NSViewController controller, string selector) => PopoverKeys.Press(Field(controller), selector);
}
