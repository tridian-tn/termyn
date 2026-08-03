using Termyn.Core.Model;

namespace Termyn.Presentation;

/// <summary>An action that acts on one task.</summary>
public enum TaskCommand
{
    /// <summary>Nothing of its own — the entry only holds the ones beneath it.</summary>
    None,

    /// <summary>Ticks a task off, or puts a completed one back. Which of the two is the row's to say.</summary>
    ToggleComplete,

    Rename,
    Due,
    Priority1,
    Priority2,
    Priority3,
    Priority4,
    Labels,
    Reminders,
    Indent,
    Outdent,
    MoveUp,
    MoveDown,
    Delete,
}

/// <summary>
/// One entry of the task menu. The view dispatches <paramref name="Command"/>, because most of them
/// end in a dialog, and pairs it with the keys that do the same thing.
/// </summary>
/// <param name="Checked">Shown as already the case — the priority the task is on.</param>
/// <param name="SeparatorBefore">Opens a new group, so related actions read together.</param>
/// <param name="Children">The entries beneath this one, for a heading that isn't itself an action.</param>
public sealed record TaskMenuEntry(
    TaskCommand Command,
    string Label,
    bool Checked = false,
    bool SeparatorBefore = false,
    IReadOnlyList<TaskMenuEntry>? Children = null);

/// <summary>
/// The actions offered on a task, in the order they should be shown.
/// </summary>
/// <remarks>
/// Here rather than in the window so the wording and the order are testable without a screen, and so
/// a second platform's menu is the same menu. What each entry is worth in keystrokes is the view's
/// business: the keys are its own, and so is how a shortcut is written down.
/// </remarks>
public static class TaskMenu
{
    /// <summary>The priorities, highest first, as the menu names them.</summary>
    private static readonly (TaskCommand Command, Priority Priority, string Label)[] Priorities =
    [
        (TaskCommand.Priority1, Priority.P1, "Priority 1 — urgent"),
        (TaskCommand.Priority2, Priority.P2, "Priority 2 — high"),
        (TaskCommand.Priority3, Priority.P3, "Priority 3 — medium"),
        (TaskCommand.Priority4, Priority.P4, "Priority 4 — none"),
    ];

    /// <summary>The menu for one task.</summary>
    public static IReadOnlyList<TaskMenuEntry> For(TaskRow row) =>
    [
        // Ticking off a task that is already done means putting it back, which is what the same
        // keystroke does — so it is one action wearing the label the row has earned.
        new(TaskCommand.ToggleComplete, row.Completed ? "Reopen" : "Complete"),

        // No ellipsis: the editor opens on the row itself rather than in a dialog.
        new(TaskCommand.Rename, "Rename"),

        new(TaskCommand.Due, "Due date…", SeparatorBefore: true),
        new(TaskCommand.None, "Priority", Children: PrioritiesFor(row)),
        new(TaskCommand.Labels, "Labels…"),

        // Left offered on a plan without them: the dialog is where the user can see what reminders
        // would give them, and it refuses the save itself rather than being silently absent here.
        new(TaskCommand.Reminders, "Reminders…"),

        new(TaskCommand.Indent, "Indent", SeparatorBefore: true),
        new(TaskCommand.Outdent, "Outdent"),
        new(TaskCommand.MoveUp, "Move up"),
        new(TaskCommand.MoveDown, "Move down"),

        // Its own group at the bottom, away from anything the user meant to click.
        new(TaskCommand.Delete, "Delete", SeparatorBefore: true),
    ];

    /// <summary>Every command the menu can raise, including the ones nested under a heading.</summary>
    public static IEnumerable<TaskCommand> Commands(IEnumerable<TaskMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Command != TaskCommand.None)
                yield return entry.Command;

            foreach (var child in Commands(entry.Children ?? []))
                yield return child;
        }
    }

    private static IReadOnlyList<TaskMenuEntry> PrioritiesFor(TaskRow row)
        => Priorities
            .Select(p => new TaskMenuEntry(p.Command, p.Label, Checked: row.Priority == p.Priority))
            .ToList();
}
