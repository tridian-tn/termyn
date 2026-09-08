using Termyn.Core.Platform;

namespace Termyn.Presentation;

/// <summary>
/// One thing the user did, in the words they would use for it.
/// </summary>
/// <param name="At">When it was first done, which for a run of edits is when the run began</param>
/// <param name="Said">The whole of it as a sentence — what was done, and to what</param>
/// <param name="Key">
/// What makes two of these the same piece of work, so a run of them reads as one. Not shown.
/// </param>
public sealed record HistoryEntry(DateTimeOffset At, string Said, string Key)
{
    public override string ToString() => Said;
}

/// <summary>
/// A gentle list of what has been done to the account this session.
/// </summary>
/// <remarks>
/// Written from what the user asked for rather than from what was sent. One intent is one line:
/// the sync engine sees commands, and a single thing a person did can be several of those, so a
/// log built from the wire would be a longer and less true account of the same afternoon.
///
/// Nothing here is navigation. Opening a project, searching, folding a task away — none of it
/// changes anything, and a list that recorded it would bury the six things that did under the
/// hundred that didn't.
///
/// In memory and for this session only. It is a record of what you have just been doing, which is
/// the question it answers well; what happened last Tuesday is the account's own activity log and
/// a different feature.
/// </remarks>
public sealed class ActionHistory
{
    /// <summary>
    /// How long a run of the same work stays open to being added to.
    /// </summary>
    /// <remarks>
    /// Sliding rather than fixed, so a description edited on and off for half an hour is one line
    /// as long as the gaps are short. What ends a run is going away and doing something else, which
    /// is also what makes the next edit worth its own line.
    /// </remarks>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>How many are kept. Beyond this the oldest goes.</summary>
    /// <remarks>Enough for an afternoon. This is a note of what you have been doing, not an archive.</remarks>
    private const int MaxEntries = 200;

    private readonly IClock _clock;
    private readonly List<HistoryEntry> _entries = [];

    /// <summary>When the newest entry was last added to, which is what the window is measured from.</summary>
    private DateTimeOffset _touched;

    public ActionHistory(IClock? clock = null) => _clock = clock ?? new SystemClock();

    /// <summary>What has been done, newest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>Raised when the list changes, so a window showing it can catch up.</summary>
    public event Action? Changed;

    /// <summary>
    /// Notes something the user did.
    /// </summary>
    /// <remarks>
    /// A run of the same work on the same thing is one line rather than many: typing into a
    /// description sends a write every time the typing pauses, and a list with forty of those in it
    /// is a list nobody reads. The line stays at the time the run started, since that is when the
    /// user would say they did it.
    /// </remarks>
    /// <param name="said">The whole of it as a sentence</param>
    /// <param name="key">What makes this the same piece of work as the one before it</param>
    public void Note(string said, string key)
    {
        var now = _clock.UtcNow;

        // Only against the newest. Doing something else in between is what closes a run off, so an
        // edit returned to after a detour is a new line and reads as one.
        if (_entries.Count > 0 && _entries[0].Key == key && now - _touched < Window)
        {
            // The wording can move on while the run doesn't — a description edited three times is
            // still one edit, but a task renamed twice should say what it is called now.
            _entries[0] = _entries[0] with { Said = said };
            _touched = now;
            Changed?.Invoke();
            return;
        }

        _entries.Insert(0, new HistoryEntry(now, said, key));
        _touched = now;

        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

        Changed?.Invoke();
    }

    /// <summary>Empties it, for a user who would rather it didn't say.</summary>
    public void Clear()
    {
        if (_entries.Count == 0)
            return;

        _entries.Clear();
        Changed?.Invoke();
    }
}
