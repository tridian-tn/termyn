namespace Termyn.Presentation;

/// <summary>
/// What the notes box said, so that Ctrl+Z can put it back.
/// </summary>
/// <remarks>
/// Windows' rich edit control has an undo queue of its own, and it cannot be used here: applying a
/// colour or a font to a stretch of text is recorded in it as an action, so a box that highlights
/// what you type answers Ctrl+Z by un-highlighting instead of by undoing. That is measurable rather
/// than suspected, and the documented way out — suspending the queue through the Text Object
/// Model — was tried and does not work: the formatting still lands on it.
///
/// So the control's own queue is switched off and this stands in for it. Here rather than in the
/// window because it is a rule about text and not about drawing: it is the same behaviour on any
/// platform, and it can be tested by typing at it in the plain.
/// </remarks>
public sealed class NotesHistory
{
    /// <summary>How many states are kept. Beyond this the oldest goes.</summary>
    private const int MaxStates = 100;

    /// <summary>
    /// How much text is kept across all of them.
    /// </summary>
    /// <remarks>
    /// A hundred states of a full-length description would be a megabyte and a half held against a
    /// box the user may have walked away from an hour ago. This is the ceiling that matters; the
    /// count is the one that usually bites first.
    /// </remarks>
    private const int MaxCharacters = 400_000;

    private readonly List<Snapshot> _states = [];

    /// <summary>Which state is on screen. Undo walks it down, redo walks it up.</summary>
    private int _at = -1;

    /// <summary>The box at one moment.</summary>
    /// <param name="Text">What it said</param>
    /// <param name="Caret">Where the caret was, so undoing puts it back where the edit happened</param>
    public readonly record struct Snapshot(string Text, int Caret);

    /// <summary>Whether there is anything to go back to.</summary>
    public bool CanUndo => _at > 0;

    /// <summary>Whether anything has been undone that could be put back.</summary>
    public bool CanRedo => _at >= 0 && _at < _states.Count - 1;

    /// <summary>
    /// Starts again on a new description, which nothing before it can be undone into.
    /// </summary>
    /// <remarks>
    /// Called whenever the box is put on another task. Without it, Ctrl+Z on a freshly opened note
    /// would replace it with the previous task's — an edit nobody made, to a task they are not
    /// looking at, saved on the next pause.
    /// </remarks>
    /// <param name="text">What the box is being filled with</param>
    public void Reset(string text)
    {
        _states.Clear();
        _states.Add(new Snapshot(text ?? string.Empty, 0));
        _at = 0;
    }

    /// <summary>
    /// Notes what the box says now, if it has changed.
    /// </summary>
    /// <remarks>
    /// Called on each pause in the typing rather than on each keystroke, so one undo takes back a
    /// phrase rather than a letter — which is what an editor does and what a keystroke-by-keystroke
    /// queue would make tedious. Anything that had been undone and not redone is dropped: typing
    /// after undoing is a new branch, and keeping the old one would let redo produce text nobody
    /// ever wrote.
    /// </remarks>
    /// <param name="text">What the box says</param>
    /// <param name="caret">Where the caret is</param>
    public void Record(string text, int caret)
    {
        var current = text ?? string.Empty;

        if (_at < 0)
        {
            Reset(current);
            return;
        }

        if (_states[_at].Text == current)
        {
            // The same words with the caret somewhere else. Worth keeping, because undoing back to
            // here should land where the user last was rather than where they were before that.
            _states[_at] = _states[_at] with { Caret = caret };
            return;
        }

        _states.RemoveRange(_at + 1, _states.Count - _at - 1);
        _states.Add(new Snapshot(current, caret));
        _at = _states.Count - 1;

        Trim();
    }

    /// <summary>
    /// The state before this one, or null when there is none.
    /// </summary>
    /// <remarks>
    /// What is on screen is noted first if it hasn't been already. Otherwise a Ctrl+Z pressed
    /// mid-sentence — before the pause that would have recorded it — would throw the sentence away
    /// rather than undo it, and redo would have nothing to put back.
    /// </remarks>
    /// <param name="current">What the box says now</param>
    /// <param name="caret">Where the caret is now</param>
    /// <returns>What to put in the box, or null when there is nothing to go back to</returns>
    public Snapshot? Undo(string current, int caret)
    {
        Record(current, caret);

        if (!CanUndo)
            return null;

        _at--;
        return _states[_at];
    }

    /// <summary>The state after this one, or null when nothing has been undone.</summary>
    /// <returns>What to put in the box, or null</returns>
    public Snapshot? Redo()
    {
        if (!CanRedo)
            return null;

        _at++;
        return _states[_at];
    }

    /// <summary>Drops the oldest states until what is held is within both ceilings.</summary>
    private void Trim()
    {
        var held = 0L;
        foreach (var state in _states)
            held += state.Text.Length;

        while (_states.Count > 1 && (_states.Count > MaxStates || held > MaxCharacters))
        {
            held -= _states[0].Text.Length;
            _states.RemoveAt(0);
            _at--;
        }

        // Everything older than the current state went, which can only happen when one state is
        // itself over the ceiling. The oldest kept is then as far back as undo goes.
        if (_at < 0)
            _at = 0;
    }
}
