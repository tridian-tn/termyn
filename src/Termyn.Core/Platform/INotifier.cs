namespace Termyn.Core.Platform;

/// <summary>One entry on the tray icon's menu.</summary>
public sealed record NotifierCommand(string Label, Action Invoke);

/// <summary>
/// The desktop's status area: an icon that says how much is due, and a menu. Kept behind an
/// interface because every desktop does this differently, and because tests of the shell shouldn't
/// put an icon in anyone's tray.
/// </summary>
public interface INotifier : IDisposable
{
    /// <summary>Raised when the user activates the icon itself — a left click on Windows.</summary>
    event Action? Activated;

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
