using Termyn.Presentation;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// The labels popover, with its view loaded but not shown: that an AppKit view can be tested here,
/// and that what it's showing is right.
/// </summary>
/// <remarks>
/// Asked for its view rather than put in a popover, which would want a window to hang from and take
/// the focus from whatever's in front. Read and clicked through the views themselves, so the list's
/// source and delegate are running for real.
/// </remarks>
public class LabelsControllerTests
{
    [AppKitFact]
    public void Its_view_lists_the_accounts_labels_with_the_tasks_ones_ticked()
    {
        var presenter = FakeAccount.Presenter();
        using var controller = new LabelsController(presenter, FakeAccount.Commands(presenter), "a", () => { });

        var boxes = Boxes(Table(controller.View));

        // "gone" isn't the account's any more, but the task wears it, so it stays offered and ticked.
        Assert.Equal(["home", "urgent", "gone"], boxes.Select(b => b.Title));
        Assert.Equal([NSCellStateValue.On, NSCellStateValue.Off, NSCellStateValue.On], boxes.Select(b => b.State));
    }

    [AppKitFact]
    public void What_is_ticked_is_applied_when_it_closes()
    {
        var presenter = FakeAccount.Presenter();
        var commands = FakeAccount.Commands(presenter);
        using var controller = new LabelsController(presenter, commands, "a", () => { });

        var urgent = Boxes(Table(controller.View))[1];
        urgent.PerformClick(urgent);
        controller.ViewWillDisappear();

        Assert.Equal(["gone", "home", "urgent"], commands.Row("a")!.Labels.Order(StringComparer.Ordinal));
    }

    [AppKitFact]
    public void Closing_it_unchanged_writes_nothing()
    {
        var presenter = FakeAccount.Presenter();
        using var controller = new LabelsController(presenter, FakeAccount.Commands(presenter), "a", () => { });
        _ = controller.View;

        controller.ViewWillDisappear();

        Assert.Empty(presenter.History.Recent());
    }

    private static NSTableView Table(NSView view)
        => Assert.IsType<NSTableView>(view.Subviews.OfType<NSScrollView>().Single().DocumentView, exactMatch: false);

    /// <summary>Each row's checkbox, made if the list hasn't needed it yet.</summary>
    private static List<NSButton> Boxes(NSTableView table)
        => Enumerable.Range(0, (int)table.RowCount)
            .Select(row => table.GetView(0, row, makeIfNecessary: true)!.Subviews.OfType<NSButton>().Single())
            .ToList();
}
