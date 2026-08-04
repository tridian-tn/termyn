namespace Termyn.Presentation;

/// <summary>
/// The description box's own state: which task it is showing, and the text it was opened with.
/// </summary>
/// <remarks>
/// Here rather than in the window because the rules are where the damage would be. A box that saves
/// when nothing was typed pushes a stale copy back over an edit made on the web; a box that decides
/// it is clean when it isn't loses what the user wrote. Both are quiet failures, and neither is
/// something a screen would show you.
/// </remarks>
public sealed class DescriptionDraft
{
    /// <summary>The task the box is on, or null when it is on none.</summary>
    public string? TaskId { get; private set; }

    /// <summary>What the box was filled with, which is what "changed" is measured against.</summary>
    public string Opened { get; private set; } = string.Empty;

    /// <summary>Puts the box on a task. Any pending edit should have been taken first.</summary>
    public void Open(string? taskId, string description)
    {
        TaskId = taskId;
        Opened = Normalised(description);
    }

    /// <summary>Whether what is in the box differs from what went into it.</summary>
    public bool IsDirty(string current) => TaskId is not null && Normalised(current) != Opened;

    /// <summary>
    /// A description in the form the account keeps it: newlines as single characters.
    /// </summary>
    /// <remarks>
    /// Text boxes on Windows hand back what they hold with a carriage return in front of every
    /// newline, and the account stores neither more nor less than the newline. Comparing the two
    /// forms would make every description look edited the moment it was shown, and saving the box's
    /// form back would grow a carriage return into the account on each round trip.
    /// </remarks>
    public static string Normalised(string text) => text.ReplaceLineEndings("\n");

    /// <summary>
    /// The edit to write, or null when there is nothing to write.
    /// </summary>
    /// <remarks>
    /// Measured against the text the box was opened with rather than whatever the account holds
    /// now: a sync may have changed this description while the box sat open, and closing the box
    /// untouched must not push the old copy back over it. Taking the edit also makes the box clean,
    /// so a save followed by a close doesn't write twice.
    /// </remarks>
    /// <param name="current">What is in the box now</param>
    /// <returns>The task and the text to save, or null when nothing was typed</returns>
    public (string TaskId, string Text)? Take(string current)
    {
        if (TaskId is not { } id || !IsDirty(current))
            return null;

        Opened = Normalised(current);
        return (id, Opened);
    }

    /// <summary>
    /// Whether a republished description can be shown, or whether it would land on top of an edit
    /// in progress. A sync runs every forty-five seconds and must not overwrite what is being typed.
    /// </summary>
    public bool CanRefresh(string current) => !IsDirty(current);
}
