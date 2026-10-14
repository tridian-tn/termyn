using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// New task, which opens the quick-add box holding where the user is looking.
/// </summary>
/// <remarks>
/// The presenter's tests say what each view starts a task with. These are the two steps either side
/// of it: the window passing that on to the menu, and the box putting it in front of the caret.
/// </remarks>
public class NewTaskTests
{
    [WinFormsFact]
    public void The_entry_is_offered_over_a_place_and_greyed_over_a_span_of_days()
    {
        using var window = Window();

        var project = Commands.StateOf(AppCommand.NewTask, window.NewContext(Node(SidebarKind.Project, "p")));
        var upcoming = Commands.StateOf(AppCommand.NewTask, window.NewContext(View(SmartView.Upcoming)));
        var filter = Commands.StateOf(AppCommand.NewTask, window.NewContext(Node(SidebarKind.Filter, "f1")));

        Assert.True(project.Enabled);
        Assert.False(upcoming.Enabled);
        Assert.False(filter.Enabled);
    }

    [WinFormsFact]
    public void The_window_hands_the_menu_what_the_view_starts_with()
    {
        using var window = Window();

        Assert.Equal("#Work", window.NewContext(Node(SidebarKind.Project, "p")).NewTaskText);
        Assert.Equal("today", window.NewContext(View(SmartView.Today)).NewTaskText);
        Assert.Null(window.NewContext(null).NewTaskText);
    }

    [WinFormsFact]
    public void The_box_holds_the_start_after_the_caret()
    {
        // So what's typed comes first: "Buy milk #Work", the way a task is written.
        using var box = Box();

        box.Fill("#Work");

        Assert.Equal(" #Work", box.Typed);
        Assert.Equal(0, box.Caret);
    }

    [WinFormsFact]
    public void The_box_summoned_with_nothing_is_empty()
    {
        // Quick add, which has always opened on an empty box, and still does after New task has
        // left something in it.
        using var box = Box();

        box.Fill("#Work");
        box.Fill(null);

        Assert.Equal(string.Empty, box.Typed);
    }

    [WinFormsFact]
    public void Enter_on_the_start_alone_adds_nothing()
    {
        // Sent as it is, the server would make a task called "#Work", or worse, "today".
        using var box = Box();

        box.Fill("#Work");

        Assert.False(box.Adds(" #Work"));
        Assert.False(box.Adds("#Work "));
        Assert.True(box.Adds("Buy milk #Work"));
    }

    [WinFormsFact]
    public void Enter_on_an_empty_box_adds_nothing_and_on_anything_typed_adds_it()
    {
        using var box = Box();

        box.Fill(null);

        Assert.False(box.Adds("   "));
        Assert.True(box.Adds("#Work"));
    }

    private static SidebarNode Node(SidebarKind kind, string id)
        => new(kind, id, id, 0, SidebarKeys.For(kind, id));

    private static SidebarNode View(SmartView view)
        => new(SidebarKind.SmartView, view.ToString(), view.ToString(), 0, SidebarKeys.For(SidebarKind.SmartView, view.ToString()), View: view);

    private static MainForm Window()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p", """{"id":"p","name":"Work","child_order":1}""");
        store.PutResource("filters", "f1", """{"id":"f1","name":"Urgent","query":"p1"}""");
        return TestWindow.Build("termyn-new-task-tests.json", store);
    }

    private static QuickAddForm Box()
    {
        var engine = new SyncEngine(new FakeApi(), new InMemorySnapshotStore(), new FakeSecrets { Stored = "tok" });
        engine.Load();
        var clock = new SystemClock();
        var presenter = new MainPresenter(engine, new QuickAddParser(clock), clock);

        var box = new QuickAddForm(presenter, Theme.From(ThemePalette.Light));
        box.CreateControl();
        return box;
    }
}
