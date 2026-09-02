namespace Termyn.Presentation;

/// <summary>
/// What the panel may do with the description of whatever it is on.
/// </summary>
/// <remarks>
/// Three refusals rather than one flag, because they don't read the same and the panel has to say
/// which it is. "Can't be edited here" is right for a completed task, whose description is perfectly
/// editable in Todoist; it would be a lie about the Inbox, where there is nowhere to edit one.
/// </remarks>
public enum DescriptionAccess
{
    /// <summary>Nothing selected that has a description at all.</summary>
    Nothing,

    /// <summary>Shown, and what is typed will be kept.</summary>
    Writable,

    /// <summary>Shown but not written to here — a completed task, held apart from the live model.</summary>
    ReadOnly,

    /// <summary>
    /// Todoist keeps no description on this at all. The Inbox, which takes the command, reports
    /// success and stores nothing — so anything typed would be lost without a word.
    /// </summary>
    NotKept,
}
