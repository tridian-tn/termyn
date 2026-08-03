using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The catalogue every surface reads from: what each command is called, and whether it can be run
/// with what is currently selected.
/// </summary>
public class CommandsTests
{
    private static TaskRow Row(Priority priority = Priority.P4, bool completed = false)
        => new("i1", "Write it up", priority, "Work", string.Empty, [], Completed: completed);

    private static SidebarNode Node(SidebarKind kind, bool favourite = false)
        => new(kind, "id", "Work", 1, "key", IsFavorite: favourite);

    private static CommandState State(AppCommand command, CommandContext? context = null)
        => Commands.StateOf(command, context ?? CommandContext.Empty);

    // ---- Every command answers -----------------------------------------------------------------

    [Fact]
    public void Every_command_has_something_to_call_itself()
    {
        // A command added to the enum and nowhere else would show as a blank row in three menus.
        var nameless = Enum.GetValues<AppCommand>()
            .Where(c => c != AppCommand.None)
            .Where(c => string.IsNullOrWhiteSpace(State(c, new CommandContext(Row(), Selection: Node(SidebarKind.Project))).Label))
            .ToList();

        Assert.Empty(nameless);
    }

    [Fact]
    public void No_label_carries_a_menu_mnemonic()
    {
        // The palette shows these as they are, so an ampersand meant for a menu would be read out
        // loud there. Menus put their own on their own headings.
        Assert.All(
            Enum.GetValues<AppCommand>().Where(c => c != AppCommand.None),
            c => Assert.DoesNotContain('&', State(c, new CommandContext(Row(), Selection: Node(SidebarKind.Project))).Label));
    }

    // ---- Which commands act on what ------------------------------------------------------------

    [Fact]
    public void The_task_commands_are_the_ones_that_need_a_task()
    {
        // IsTaskCommand is a range check over the enum, so a command inserted in the wrong place
        // silently joins or leaves the group. This is what notices.
        var needsATask = Enum.GetValues<AppCommand>()
            .Where(c => c != AppCommand.None)
            .Where(c => !State(c).Enabled && State(c, new CommandContext(Row(), new TaskAbilities(true, true, true, true))).Enabled)
            .ToList();

        Assert.Equal(needsATask, needsATask.Where(Commands.IsTaskCommand).ToList());
    }

    [Fact]
    public void The_selection_commands_are_the_ones_that_need_a_sidebar_row()
    {
        Assert.All(
            Enum.GetValues<AppCommand>().Where(Commands.IsSelectionCommand),
            c =>
            {
                Assert.False(State(c).Enabled);
                Assert.True(State(c, new CommandContext(Selection: Node(SidebarKind.Project))).Enabled);
            });
    }

    // ---- Labels that move ----------------------------------------------------------------------

    [Fact]
    public void Completing_becomes_reopening_on_a_task_that_is_done()
    {
        Assert.Equal("Complete", State(AppCommand.ToggleComplete, new CommandContext(Row())).Label);
        Assert.Equal("Reopen", State(AppCommand.ToggleComplete, new CommandContext(Row(completed: true))).Label);
    }

    [Fact]
    public void Showing_completed_tasks_becomes_hiding_them_once_they_are_shown()
    {
        Assert.Equal("Show completed tasks", State(AppCommand.ToggleCompleted).Label);
        Assert.Equal("Hide completed tasks", State(AppCommand.ToggleCompleted, new CommandContext(ShowingCompleted: true)).Label);
    }

    [Theory]
    [InlineData(SidebarKind.Project, "Rename project")]
    [InlineData(SidebarKind.Section, "Rename section")]
    [InlineData(SidebarKind.Label, "Rename label")]
    public void Renaming_is_named_for_what_it_would_rename(SidebarKind kind, string expected)
        => Assert.Equal(expected, State(AppCommand.RenameSelection, new CommandContext(Selection: Node(kind))).Label);

    [Fact]
    public void The_favourite_toggle_says_which_way_it_would_go()
    {
        Assert.Equal("Add to favourites", State(AppCommand.ToggleFavourite, new CommandContext(Selection: Node(SidebarKind.Project))).Label);
        Assert.Equal(
            "Remove from favourites",
            State(AppCommand.ToggleFavourite, new CommandContext(Selection: Node(SidebarKind.Project, favourite: true))).Label);
    }

    // ---- What is greyed ------------------------------------------------------------------------

    [Fact]
    public void A_task_that_cannot_move_is_not_offered_the_move()
    {
        var stuck = new CommandContext(Row(), new TaskAbilities(CanMoveUp: true));

        Assert.True(State(AppCommand.MoveUp, stuck).Enabled);
        Assert.False(State(AppCommand.MoveDown, stuck).Enabled);
        Assert.False(State(AppCommand.Indent, stuck).Enabled);
        Assert.False(State(AppCommand.Outdent, stuck).Enabled);
    }

    [Fact]
    public void Abilities_without_a_task_count_for_nothing()
    {
        // Nothing selected, but the abilities of the row that was: a stale pairing must not leave
        // Move up offered over an empty outline.
        var stale = new CommandContext(Task: null, Abilities: new TaskAbilities(true, true, true, true));

        Assert.False(State(AppCommand.MoveUp, stale).Enabled);
        Assert.False(State(AppCommand.Indent, stale).Enabled);
    }

    [Theory]
    [InlineData(SidebarKind.SmartView)]
    [InlineData(SidebarKind.Filter)]
    [InlineData(SidebarKind.Header)]
    public void A_row_that_is_not_ours_to_edit_offers_nothing(SidebarKind kind)
    {
        var context = new CommandContext(Selection: Node(kind));

        Assert.False(State(AppCommand.RenameSelection, context).Enabled);
        Assert.False(State(AppCommand.DeleteSelection, context).Enabled);
        Assert.False(State(AppCommand.ToggleFavourite, context).Enabled);
    }

    [Fact]
    public void A_section_can_be_renamed_but_has_no_star()
    {
        var section = new CommandContext(Selection: Node(SidebarKind.Section));

        Assert.True(State(AppCommand.RenameSelection, section).Enabled);
        Assert.True(State(AppCommand.DeleteSelection, section).Enabled);
        Assert.False(State(AppCommand.ToggleFavourite, section).Enabled);
    }

    [Fact]
    public void A_new_section_needs_a_project_under_the_sidebar()
    {
        Assert.False(State(AppCommand.NewSection).Enabled);
        Assert.False(State(AppCommand.NewSection, new CommandContext(Selection: Node(SidebarKind.Label))).Enabled);
        Assert.True(State(AppCommand.NewSection, new CommandContext(Selection: Node(SidebarKind.Project))).Enabled);
    }

    [Fact]
    public void The_priority_the_task_is_on_is_the_one_ticked()
    {
        var context = new CommandContext(Row(Priority.P2));

        Assert.True(State(AppCommand.Priority2, context).Checked);
        Assert.False(State(AppCommand.Priority1, context).Checked);
        Assert.False(State(AppCommand.Priority2, CommandContext.Empty).Checked);
    }

    // ---- What the presenter answers ------------------------------------------------------------

    [Fact]
    public void The_presenter_reports_what_the_task_at_each_end_can_do()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "a", """{"id":"a","content":"A","project_id":"p","child_order":1}""");
        store.PutResource("items", "b", """{"id":"b","content":"B","project_id":"p","child_order":2}""");
        var presenter = NewPresenter(store);

        var first = presenter.AbilitiesFor("a");
        var second = presenter.AbilitiesFor("b");

        Assert.Equal(new TaskAbilities(CanIndent: false, CanOutdent: false, CanMoveUp: false, CanMoveDown: true), first);
        Assert.Equal(new TaskAbilities(CanIndent: true, CanOutdent: false, CanMoveUp: true, CanMoveDown: false), second);
    }

    [Fact]
    public void Nothing_selected_can_do_nothing()
        => Assert.Equal(TaskAbilities.None, NewPresenter(new InMemorySnapshotStore()).AbilitiesFor(null));

    [Fact]
    public void A_task_the_account_no_longer_holds_can_do_nothing()
        => Assert.Equal(TaskAbilities.None, NewPresenter(new InMemorySnapshotStore()).AbilitiesFor("gone"));

    private static MainPresenter NewPresenter(InMemorySnapshotStore store)
    {
        var today = new DateOnly(2026, 7, 31);
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(today));
        engine.Load();
        return new MainPresenter(engine, new QuickAddParser(new FixedClock(today)));
    }
}
