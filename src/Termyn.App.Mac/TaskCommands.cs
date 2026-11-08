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
    /// The task commands this window offers. The rest of <see cref="Menus.TaskContext"/> —
    /// deadlines and reminders — comes in a later version.
    /// </summary>
    public static readonly IReadOnlySet<AppCommand> Offered = new HashSet<AppCommand>
    {
        AppCommand.ToggleComplete,
        AppCommand.Rename,
        AppCommand.NewSubtask,
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
        AppCommand.Labels,
        AppCommand.Indent,
        AppCommand.Outdent,
        AppCommand.MoveUp,
        AppCommand.MoveDown,
        AppCommand.MoveTo,
        AppCommand.ShowInTodoist,
        AppCommand.Delete,
    };

    /// <summary>Opens the in-place editor on a task's name. Set by the task list.</summary>
    public Action<string>? BeginRename { get; set; }

    /// <summary>Opens the due-date popover on a task. Set by the window.</summary>
    public Action<string>? OpenDue { get; set; }

    /// <summary>Opens the new-task popover. Set by the window.</summary>
    public Action? OpenNewTask { get; set; }

    /// <summary>Opens the new-task popover on a task, to add a sub-task to it. Set by the window.</summary>
    public Action<string>? OpenNewSubtask { get; set; }

    /// <summary>Opens the labels popover on a task. Set by the window.</summary>
    public Action<string>? OpenLabels { get; set; }

    /// <summary>Opens the popover that moves a task to another project or section. Set by the window.</summary>
    public Action<string>? OpenMoveTo { get; set; }

    /// <summary>Puts the selection on a task once the rows it's in have arrived. Set by the task list.</summary>
    public Action<string>? SelectWhenShown { get; set; }

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
        AppCommand.NewSubtask => Strings.NewSubtask,
        AppCommand.Labels => Strings.Labels,
        AppCommand.Indent => Strings.Indent,
        AppCommand.Outdent => Strings.Outdent,
        AppCommand.MoveUp => Strings.MoveUp,
        AppCommand.MoveDown => Strings.MoveDown,
        AppCommand.MoveTo => Strings.MoveTo,
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

            case AppCommand.NewSubtask:
                OpenNewSubtask?.Invoke(id);
                break;

            case AppCommand.Labels:
                OpenLabels?.Invoke(id);
                break;

            case AppCommand.Indent:
                Guarded(() =>
                {
                    if (!presenter.Indent(id))
                        say(Refusal(command, id)!);
                });
                break;

            case AppCommand.Outdent:
                Guarded(() =>
                {
                    if (!presenter.Outdent(id))
                        say(Refusal(command, id)!);
                });
                break;

            case AppCommand.MoveUp:
            case AppCommand.MoveDown:
                Guarded(() =>
                {
                    if (!presenter.Move(id, command == AppCommand.MoveUp ? -1 : 1))
                        say(Refusal(command, id)!);
                });
                break;

            case AppCommand.MoveTo:
                OpenMoveTo?.Invoke(id);
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

    /// <summary>
    /// Says why a key got no answer, when it's one of the commands that can be refused for a reason
    /// worth knowing.
    /// </summary>
    /// <remarks>
    /// The menu greys these when they can't run, which is right for the menu but leaves the key with
    /// nothing to do: AppKit won't send a disabled item's action, so ⌘] on a task with nothing above
    /// it would otherwise just beep. The list asks this first. A turned-away token greys them too,
    /// but that's said by the banner, and has nothing to do with where the task sits.
    /// </remarks>
    /// <param name="command">The command the key belongs to</param>
    /// <param name="taskId">The selected task, or null for none</param>
    /// <returns>True when it was explained, which is the key dealt with</returns>
    public bool Explain(AppCommand command, string? taskId)
    {
        if (taskId is null
            || presenter.ReconnectNeeded
            || State(command, taskId).Enabled
            || Refusal(command, taskId) is not { } why)
        {
            return false;
        }

        say(why);
        return true;
    }

    /// <summary>
    /// Why a command can't be done to a task. For indenting, which of the two reasons it is, since
    /// they send the reader different places: one is "not here" and the other is "not ever", as on
    /// Windows.
    /// </summary>
    private string? Refusal(AppCommand command, string id) => command switch
    {
        AppCommand.Indent => presenter.IndentTooDeep(id) ? Strings.IndentTooDeep : Strings.IndentNothingAbove,
        AppCommand.Outdent => Strings.IndentTopLevel,
        AppCommand.MoveUp or AppCommand.MoveDown => Strings.MoveAtEnd,
        AppCommand.MoveTo => Strings.MoveCompletedRefused,
        _ => null,
    };

    /// <summary>
    /// What a task is called now, given what it may once have been called: one added a moment ago
    /// is renamed when the sync hears what the server calls it.
    /// </summary>
    public string CurrentIdOf(string id) => presenter.CurrentIdOf(id);

    /// <summary>Renames a task, once its name has been edited where it stands.</summary>
    public void Rename(string id, string name) => Guarded(() => presenter.Rename(id, name));

    /// <summary>
    /// Gives a task the labels ticked in the picker, making any the account doesn't have yet.
    /// </summary>
    /// <remarks>
    /// Closing the picker unchanged is not an edit: writing anyway would queue a command that owns
    /// the task until it lands, and a web-side edit arriving meanwhile would be dropped for nothing.
    /// A name the task already wears is left alone even if the account doesn't list it, since the
    /// picker carries those so they aren't stripped, not so they're adopted.
    ///
    /// Compared with what the picker opened on rather than the task's row, which a sync can take out
    /// of the view while the picker's open, and what was ticked still stands.
    /// </remarks>
    /// <param name="id">The task</param>
    /// <param name="opened">The labels it had when the picker opened</param>
    /// <param name="picked">Every label it should have</param>
    public void SetLabels(string id, IReadOnlyList<string> opened, IReadOnlyList<string> picked)
    {
        if (picked.Count == opened.Count && !picked.Except(opened, StringComparer.OrdinalIgnoreCase).Any())
            return;

        Guarded(() =>
        {
            var known = presenter.Labels.Select(l => l.Name).ToList();
            var fresh = picked
                .Where(p => !known.Contains(p, StringComparer.OrdinalIgnoreCase))
                .Where(p => !opened.Contains(p, StringComparer.OrdinalIgnoreCase));

            foreach (var name in fresh)
                presenter.AddLabel(name);

            presenter.SetLabels(id, picked);
        });
    }

    /// <summary>
    /// Moves a task somewhere else, and stays on it if the view still shows it, or says where it
    /// went if not.
    /// </summary>
    /// <remarks>
    /// False from the presenter means something changed while the picker was open: it won't
    /// confirm the place the task already sits, so what's gone is the task or where it was going.
    /// </remarks>
    /// <param name="id">The task</param>
    /// <param name="destination">Where to</param>
    public void MoveTo(string id, MoveDestination destination) => Guarded(() =>
    {
        if (!presenter.MoveTo(id, destination))
            say(Strings.MoveTargetGone);
        else if (Row(id) is not null)
            SelectWhenShown?.Invoke(id);
        else
            say(string.Format(CultureInfo.CurrentCulture, Strings.MovedNotShown, destination.Path));
    });

    /// <summary>
    /// Stays on a sub-task just added if the view shows it, or says it won't. A row that never
    /// appears reads as a task that was never created.
    /// </summary>
    /// <param name="id">The new task</param>
    public void Added(string id)
    {
        if (Row(id) is not null)
            SelectWhenShown?.Invoke(id);
        else
            say(Strings.AddedNotShown);
    }

    /// <summary>Whether a dragged task can go where the pointer is, so the pointer can say so.</summary>
    public bool CanDrop(string id, DropTarget target) => presenter.CanDrop(id, target);

    /// <summary>Puts a dragged task down, or says that something changed while it was being dragged.</summary>
    /// <returns>True when it moved</returns>
    public bool Drop(string id, DropTarget target)
    {
        var dropped = false;

        // Only when it was turned away. One that threw has already said what went wrong.
        if (Guarded(() => dropped = presenter.Drop(id, target)) && !dropped)
            say(Strings.DragTaskFailed);
        return dropped;
    }

    /// <summary>Whether a dragged task can be moved to a row of the sidebar.</summary>
    public bool CanDropTaskOn(string id, SidebarNode node) => presenter.CanDropTaskOn(id, node);

    /// <summary>Moves a dragged task to a project or section in the sidebar.</summary>
    /// <returns>True when it moved</returns>
    public bool DropTaskOn(string id, SidebarNode node)
    {
        var dropped = false;

        // Only when it was turned away. One that threw has already said what went wrong.
        if (Guarded(() => dropped = presenter.DropTaskOn(id, node)) && !dropped)
            say(Strings.DragTaskFailed);
        return dropped;
    }

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
