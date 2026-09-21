namespace Termyn.Core.Platform;

/// <summary>One entry on the tray icon's menu, or the rule between two groups of them.</summary>
/// <param name="Label">What the entry says</param>
/// <param name="Invoke">What picking it does</param>
public sealed record NotifierCommand(string Label, Action Invoke)
{
    /// <summary>A rule between groups of entries, which does nothing if a desktop lets it be picked.</summary>
    public static NotifierCommand Rule { get; } = new(string.Empty, () => { }) { IsRule = true };

    /// <summary>
    /// Whether this is a rule rather than something to run.
    /// </summary>
    /// <remarks>
    /// Said outright rather than read off an empty label: entries are named from the account now,
    /// and a project saved with no name at all would otherwise arrive here as a rule — drawn as a
    /// line, and unreachable.
    /// </remarks>
    public bool IsRule { get; init; }
}

/// <summary>
/// The desktop's status area: an icon that says how much is due, and a menu. Kept behind an
/// interface because every desktop does this differently, and because tests of the shell shouldn't
/// put an icon in anyone's tray.
/// </summary>
public interface INotifier : IDisposable
{
    /// <summary>Raised when the user activates the icon itself — a left click on Windows.</summary>
    event Action? Activated;

    /// <summary>
    /// Raised as the menu is about to be shown, so what it offers can be worked out then.
    /// </summary>
    /// <remarks>
    /// The entries follow what the user has been doing, and rebuilding them on a timer means either
    /// showing something stale or rewriting the menu under the pointer while it is open. Asked for
    /// here, they are right at the only moment anybody sees them.
    /// </remarks>
    event Action? MenuOpening;

    /// <summary>Whether the icon is currently in the status area.</summary>
    bool Visible { get; set; }

    /// <summary>
    /// Updates the icon's hover text and the count it badges.
    /// </summary>
    /// <param name="dueToday">Tasks due today; zero shows a plain icon.</param>
    void SetStatus(string tooltip, int dueToday);

    /// <summary>Replaces the icon's menu.</summary>
    void SetCommands(IReadOnlyList<NotifierCommand> commands);
}
