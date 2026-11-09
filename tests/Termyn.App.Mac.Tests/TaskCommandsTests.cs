using Termyn.Core.Api;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Mac.Tests;

/// <summary>
/// What a command is called on a Mac and how it stands over a task, as the menus read them.
/// </summary>
public class TaskCommandsTests
{
    private readonly TaskCommands _commands = FakeAccount.Commands(FakeAccount.Presenter());

    [AppKitFact]
    public void A_task_command_is_greyed_with_nothing_selected_and_lit_with_a_task()
    {
        Assert.False(_commands.State(AppCommand.Rename, null).Enabled);
        Assert.True(_commands.State(AppCommand.Rename, "a").Enabled);
    }

    [AppKitFact]
    public void The_priority_a_task_has_is_ticked_and_the_others_arent()
    {
        Assert.True(_commands.State(AppCommand.Priority1, "a").Checked);
        Assert.False(_commands.State(AppCommand.Priority2, "a").Checked);
    }

    [AppKitFact]
    public void Indenting_is_lit_only_with_a_task_above_to_go_under()
    {
        Assert.False(_commands.State(AppCommand.Indent, "a").Enabled);
        Assert.True(_commands.State(AppCommand.Indent, "b").Enabled);
    }

    [AppKitFact]
    public void Deadline_and_Reminders_open_their_popovers_on_the_task()
    {
        var opened = new List<string>();
        _commands.OpenDeadline = id => opened.Add("deadline " + id);
        _commands.OpenReminders = id => opened.Add("reminders " + id);

        _commands.Run(AppCommand.Deadline, "a");
        _commands.Run(AppCommand.Reminders, "b");

        Assert.Equal(["deadline a", "reminders b"], opened);
        Assert.Equal("Deadline…", TaskCommands.Label(AppCommand.Deadline, _commands.State(AppCommand.Deadline, "a"), _commands.Row("a")));
        Assert.Equal("Reminders…", TaskCommands.Label(AppCommand.Reminders, _commands.State(AppCommand.Reminders, "a"), _commands.Row("a")));
    }

    [AppKitFact]
    public void Reminders_is_offered_on_a_plan_without_them()
    {
        // The popover is where the user can see what they'd be getting, and it won't let a save through.
        var commands = FakeAccount.Commands(FakeAccount.Presenter(plan: ("free", false)));

        Assert.True(commands.State(AppCommand.Reminders, "a").Enabled);
    }

    [AppKitFact]
    public void Taking_the_date_off_is_lit_only_on_a_task_with_one()
    {
        Assert.False(_commands.State(AppCommand.DueNone, "a").Enabled);
        Assert.True(_commands.State(AppCommand.DueNone, "b").Enabled);
    }

    [AppKitFact]
    public async Task A_turned_away_token_greys_what_would_write_but_not_what_only_looks()
    {
        var api = new FakeApi();
        var presenter = FakeAccount.Presenter(api);
        var commands = FakeAccount.Commands(presenter);

        api.Throw = new TodoistAuthException("rejected");
        await Assert.ThrowsAsync<TodoistAuthException>(() => presenter.SyncAsync(TestContext.Current.CancellationToken));

        Assert.False(commands.State(AppCommand.Rename, "a").Enabled);
        Assert.True(commands.State(AppCommand.ShowInTodoist, "a").Enabled);
    }

    [AppKitFact]
    public void Ticking_off_is_named_for_how_the_task_stands()
    {
        var open = _commands.Row("a")!;
        var done = open with { Completed = true };
        var state = _commands.State(AppCommand.ToggleComplete, "a");

        Assert.Equal("Mark as Completed", TaskCommands.Label(AppCommand.ToggleComplete, state, open));
        Assert.Equal("Mark as Incomplete", TaskCommands.Label(AppCommand.ToggleComplete, state, done));
    }

    [AppKitFact]
    public void A_priority_is_named_by_its_number()
    {
        Assert.Equal("Priority 1", Label(AppCommand.Priority1));
        Assert.Equal("Priority 4", Label(AppCommand.Priority4));
    }

    [AppKitFact]
    public void The_Macs_own_words_win_over_the_portable_ones()
    {
        var state = _commands.State(AppCommand.Due, "a");

        Assert.Equal("Due Date…", TaskCommands.Label(AppCommand.Due, state, _commands.Row("a")));
        Assert.NotEqual(state.Label, TaskCommands.Label(AppCommand.Due, state, _commands.Row("a")));
    }

    [AppKitFact]
    public void A_command_with_no_words_of_its_own_keeps_the_portable_ones()
        => Assert.Equal("As it stands", TaskCommands.Label(AppCommand.QuickAdd, new CommandState("As it stands", true), null));

    [AppKitFact]
    public void Every_command_on_offer_has_a_name()
        => Assert.All(TaskCommands.Offered, command => Assert.False(string.IsNullOrWhiteSpace(Label(command)), command.ToString()));

    private string Label(AppCommand command) => TaskCommands.Label(command, _commands.State(command, "a"), _commands.Row("a"));
}
