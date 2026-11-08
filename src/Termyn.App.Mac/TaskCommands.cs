using System.Globalization;
using Termyn.Core;
using Termyn.Core.Api;
using Termyn.Core.Logging;
using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Mac;

/// <summary>
/// The one way into an edit, whether it came from the menu bar, a right-click, the toolbar or a key.
/// </summary>
/// <remarks>
/// What a command is called, whether it can run and whether it's ticked come from
/// <see cref="Commands.StateOf"/>, which the Windows menus read too — only the wording is the
/// Mac's own, in title case the way Mac menus write it. What running one does is the same presenter
/// call the Windows window makes, wrapped so a failure is said in the window rather than thrown out
/// of a menu.
/// </remarks>
internal sealed class TaskCommands(MainPresenter presenter, ILog log, Action<string> say)
{
    /// <summary>
    /// The task commands this window offers. The rest of <see cref="Menus.TaskContext"/> — labels,
    /// moving, nesting, deadlines, reminders — comes in a later version.
    /// </summary>
    public static readonly IReadOnlySet<AppCommand> Offered = new HashSet<AppCommand>
    {
        AppCommand.ToggleComplete,
        AppCommand.Rename,
        AppCommand.Due,
        AppCommand.DueToday,
        AppCommand.DueTomorrow,
        AppCommand.DueWeekend,
        AppCommand.DueNextWeek,
        AppCommand.DueNone,
        AppCommand.Priority1,
        AppCommand.Priority2,
        AppCommand.Priority3,
        AppCommand.Priority4,
        AppCommand.ShowInTodoist,
        AppCommand.Delete,
    };

    /// <summary>Opens the in-place editor on a task's name. Set by the task list.</summary>
    public Action<string>? BeginRename { get; set; }

    /// <summary>Opens the due-date popover on a task. Set by the window.</summary>
    public Action<string>? OpenDue { get; set; }

    /// <summary>Opens the new-task popover. Set by the window.</summary>
    public Action? OpenNewTask { get; set; }

    /// <summary>What the presenter would say of a command over this task, as the menus read it.</summary>
    /// <param name="command">The command</param>
    /// <param name="taskId">The selected task, or null for none</param>
    public CommandState State(AppCommand command, string? taskId) => Commands.StateOf(command, Context(taskId));

    /// <summary>The selected task's row, or null when nothing is selected or it's a day's heading.</summary>
    public TaskRow? Row(string? taskId)
        => taskId is null ? null : presenter.Rows.FirstOrDefault(r => r.Id == taskId && !r.IsHeading);

    /// <summary>What a command is called on a Mac, in the words and case its menus use.</summary>
    /// <param name="command">The command</param>
    /// <param name="state">How it stands, for the entries this window has no words of its own for</param>
    /// <param name="task">The task it's for, which the toggle's name follows</param>
    public static string Label(AppCommand command, CommandState state, TaskRow? task) => command switch
    {
        // Reminders' words, since a ticked box is the same idea there.
        AppCommand.ToggleComplete => task is { Completed: true } ? Strings.MarkIncomplete : Strings.MarkCompleted,
        AppCommand.Rename => Strings.Rename,
        AppCommand.Due => Strings.DueDate,
        AppCommand.DueToday => Strings.DueToday,
        AppCommand.DueTomorrow => Strings.DueTomorrow,
        AppCommand.DueWeekend => Strings.DueWeekend,
        AppCommand.DueNextWeek => Strings.DueNextWeek,
        AppCommand.DueNone => Strings.DueNone,
        AppCommand.Priority1 or AppCommand.Priority2 or AppCommand.Priority3 or AppCommand.Priority4
            => string.Format(CultureInfo.CurrentCulture, Strings.PriorityN, (int)(Commands.PriorityOf(command) ?? Priority.P4)),
        AppCommand.ShowInTodoist => Strings.ShowInTodoist,
        AppCommand.Delete => Strings.Delete,
        AppCommand.NewTask => Strings.MenuNewTask,
        _ => state.Label,
    };

    /// <summary>
    /// Runs a command on a task, if it can run now.
    /// </summary>
    /// <remarks>
    /// Asked again here rather than trusted to whatever reached it: a menu is checked when it
    /// opens, but Space and Return in the list reach this with no menu having been asked.
    /// </remarks>
    /// <param name="command">The command</param>
    /// <param name="taskId">The task it's for, or null for the commands that need none</param>
    public void Run(AppCommand command, string? taskId)
    {
        if (!State(Gate(command), taskId).Enabled)
            return;

        switch (command)
        {
            case AppCommand.NewTask:
                OpenNewTask?.Invoke();
                return;

            case AppCommand.Undo:
                Guarded(() =>
                {
                    if (!presenter.Undo())
                        say(Strings.NothingToUndo);
                });
                return;
        }

        if (taskId is not { } id)
            return;

        switch (command)
        {
            // Ticking off a task that's already done means putting it back.
            case AppCommand.ToggleComplete:
                Guarded(() =>
                {
                    if (Row(id) is { Completed: true })
                        presenter.Reopen(id);
                    else
                        presenter.Complete(id);
                });
                break;

            case AppCommand.Rename:
                BeginRename?.Invoke(id);
                break;

            case AppCommand.Due:
                OpenDue?.Invoke(id);
                break;

            case AppCommand.DueToday:
            case AppCommand.DueTomorrow:
            case AppCommand.DueWeekend:
            case AppCommand.DueNextWeek:
                Guarded(() => _ = presenter.ScheduleDays.For(command) is { } day && presenter.Reschedule(id, day));
                break;

            // Nothing to write on a task with no due date, which the menu greys it for.
            case AppCommand.DueNone:
                Guarded(() =>
                {
                    if (presenter.DueWritten(id).Length > 0)
                        presenter.SetDue(id, null);
                });
                break;

            case AppCommand.Priority1:
            case AppCommand.Priority2:
            case AppCommand.Priority3:
            case AppCommand.Priority4:
                Guarded(() => presenter.SetPriority(id, Commands.PriorityOf(command) ?? Priority.P4));
                break;

            case AppCommand.ShowInTodoist:
                if (Links.Openable(presenter.TodoistLinkFor(id)) is { } link)
                    NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(link));
                else
                    say(Strings.ShowInTodoistNotYet);
                break;

            // Not confirmed, as on Windows: ⌘Z brings the task back.
            case AppCommand.Delete:
                Guarded(() => presenter.Delete(id));
                break;
        }
    }

    /// <summary>Renames a task, once its name has been edited where it stands.</summary>
    public void Rename(string id, string name) => Guarded(() => presenter.Rename(id, name));

    /// <summary>How the list is ordered, for the header's arrow and the ticks in Sort By.</summary>
    public TaskSort Sort => presenter.Sort;

    /// <summary>Orders the list by a column as a click on its header does: the one in use turns round.</summary>
    public void SortBy(TaskColumn column) => Guarded(() => presenter.SortBy(column));

    /// <summary>Orders the list exactly as an item in Sort By names it.</summary>
    public void SortBy(TaskSort sort) => Guarded(() => presenter.SortBy(sort));

    /// <summary>Hides or shows what is filed under a task.</summary>
    public void Fold(string id, bool collapsed) => Guarded(() => presenter.SetCollapsed(id, collapsed));

    /// <summary>Hides or shows what is filed under every task in the view.</summary>
    public void FoldAll(bool collapsed) => Guarded(() => presenter.FoldAll(collapsed));

    /// <summary>
    /// Runs a change, and says what went wrong rather than throwing it out of a menu or a key.
    /// </summary>
    /// <remarks>
    /// A turned-away token isn't said here: the window's subtitle and banner already say it, and
    /// the menus have greyed everything that would write.
    /// </remarks>
    /// <param name="change">The change</param>
    /// <returns>True when it went through</returns>
    public bool Guarded(Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (TodoistAuthException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error("A change couldn't be made.", ex);
            say(string.Format(CultureInfo.CurrentCulture, Strings.ChangeFailed, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// The command whose state says whether this one can run.
    /// </summary>
    /// <remarks>
    /// New Task on a Mac is Windows' Quick Add as much as its New Task: it opens the same box in
    /// every view, filled in where the view has something to fill in. Windows greys its New Task
    /// where there's nothing to fill in with, which here would grey ⌘N in Today.
    /// </remarks>
    public static AppCommand Gate(AppCommand command) => command == AppCommand.NewTask ? AppCommand.QuickAdd : command;

    /// <summary>What the menus are asked against, as the Windows window builds it.</summary>
    private CommandContext Context(string? taskId) => new(
        Task: Row(taskId),
        Abilities: presenter.AbilitiesFor(taskId),
        ShowingCompleted: presenter.ShowingCompleted,
        CanUndo: presenter.CanUndo,
        CanExpandAll: presenter.CanExpandAll,
        CanCollapseAll: presenter.CanCollapseAll,
        Sort: presenter.Sort,
        NewTaskText: presenter.NewTaskText(presenter.Selection),
        ReconnectNeeded: presenter.ReconnectNeeded,
        Schedule: presenter.ScheduleDays);
}
