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
    public void Completed_tasks_keeps_one_name_and_says_the_rest_with_the_tick()
    {
        // It used to read "Show completed tasks" and then "Hide completed tasks". A surface that
        // can draw a tick has no use for that, and one that can't is better told the state than
        // handed a spelling of it — so the state lives in Checked and the name stands still.
        Assert.Equal("Completed tasks", State(AppCommand.ToggleCompleted).Label);
        Assert.Equal("Completed tasks", State(AppCommand.ToggleCompleted, new CommandContext(ShowingCompleted: true)).Label);

        Assert.False(State(AppCommand.ToggleCompleted).Checked);
        Assert.True(State(AppCommand.ToggleCompleted, new CommandContext(ShowingCompleted: true)).Checked);
    }

    [Fact]
    public void Zooming_is_offered_only_where_there_is_something_to_zoom()
    {
        // The panel scales the description, reading or writing. The comments it also shows are
        // drawn rather than set in text it can scale, so there is nothing to offer over those.
        Assert.False(State(AppCommand.ZoomIn).Enabled);
        Assert.False(State(AppCommand.ZoomOut).Enabled);

        var open = new CommandContext(ShowingDescription: true);
        Assert.True(State(AppCommand.ZoomIn, open).Enabled);
        Assert.True(State(AppCommand.ZoomOut, open).Enabled);

        var comments = new CommandContext(ShowingDescription: true, ShowingComments: true);
        Assert.False(State(AppCommand.ZoomIn, comments).Enabled);
        Assert.False(State(AppCommand.ZoomOut, comments).Enabled);
    }

    [Fact]
    public void The_way_back_to_the_default_zoom_is_offered_only_once_there_is_one()
    {
        // Greyed until the panel has been scaled, which is also how the entry says whether it is at
        // its own size — the same as Default order.
        var open = new CommandContext(ShowingDescription: true);
        var zoomed = new CommandContext(ShowingDescription: true, Zoomed: true);

        Assert.False(State(AppCommand.ZoomReset, open).Enabled);
        Assert.True(State(AppCommand.ZoomReset, zoomed).Enabled);

        // And never over the comments, where there is nothing scaled to put back.
        Assert.False(State(AppCommand.ZoomReset, new CommandContext(ShowingDescription: true, ShowingComments: true, Zoomed: true)).Enabled);
    }

    [Fact]
    public void No_command_in_the_catalogue_renames_itself_for_its_own_state()
    {
        // The rule this keeps: a label may change with what is selected — "Rename project" against
        // "Rename label" — but never with whether the thing it does is already on. Anything that
        // did would read as a different entry each time you looked at it, and would leave the
        // palette, which has no tick, spelling the state a second way.
        foreach (var command in Enum.GetValues<AppCommand>().Where(c => c != AppCommand.None))
        {
            var off = State(command);
            var on = State(command, new CommandContext(
                ShowingCompleted: true,
                ShowingDescription: true,
                WritingDescription: true,
                ShowingComments: true));

            Assert.Equal(off.Label, on.Label);
        }
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
