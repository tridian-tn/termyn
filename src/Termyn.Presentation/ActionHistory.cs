using Termyn.Core.History;
using Termyn.Core.Platform;

namespace Termyn.Presentation;

/// <summary>
/// One thing the user did, in the words they would use for it.
/// </summary>
/// <param name="At">When it was first done, which for a run of edits is when the run began</param>
/// <param name="Said">The whole of it as a sentence — what was done, and to what</param>
public sealed record HistoryEntry(DateTimeOffset At, string Said)
{
    public override string ToString() => Said;
}

/// <summary>
/// A gentle list of what has been done to the account, kept in a file of its own.
/// </summary>
/// <remarks>
/// Written from what the user asked for rather than from what was sent. One intent is one line: the
/// sync engine sees commands, and a single thing a person did can be several of those, so a log
/// built from the wire would be a longer and less true account of the same afternoon.
///
/// Nothing here is navigation. Opening a project, searching, folding a task away — none of it
/// changes anything, and a list that recorded it would bury the six things that did under the
/// hundred that didn't.
///
/// Almost none of it is in memory. What is held is the run currently being added to and the handful
/// behind it waiting to be written; everything else is read back from the file, and only when
/// somebody asks to see it. A month of somebody's afternoons has no business sitting in a process
/// that mostly wants to be small.
/// </remarks>
public sealed class ActionHistory : IDisposable
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

    /// <summary>How long an entry is kept before the housekeeping takes it.</summary>
    private static readonly TimeSpan Keep = TimeSpan.FromDays(31);

    /// <summary>How often the housekeeping is worth doing.</summary>
    /// <remarks>
    /// A month is a long time to be wrong about, so there is no hurry. Done on the way up and then
    /// once an hour, which for a window left open all week is a handful of deletes.
    /// </remarks>
    private static readonly TimeSpan Sweeping = TimeSpan.FromHours(1);

    /// <summary>How many may wait in memory before they are written whatever the clock says.</summary>
    /// <remarks>
    /// A backstop rather than the usual path: the flush comes round with the sync loop. This is for
    /// somebody working faster than that, so the most a sudden end can lose stays small.
    /// </remarks>
    private const int MostPending = 20;

    /// <summary>How many are read back for the viewer.</summary>
    /// <remarks>Enough to scroll through and stop well short of loading a month into a list box.</remarks>
    private const int MostShown = 500;

    private readonly IHistoryStore? _store;
    private readonly IClock _clock;
    private readonly Lock _gate = new();

    /// <summary>The run being added to, which is the only entry whose wording can still change.</summary>
    private HistoryEntry? _open;

    private string? _openKey;
    private DateTimeOffset _touched;

    /// <summary>Closed and waiting to be written, oldest first.</summary>
    private readonly List<HistoryEntry> _pending = [];

    private DateTimeOffset _swept;

    public ActionHistory(IHistoryStore? store = null, IClock? clock = null)
    {
        _store = store;
        _clock = clock ?? new SystemClock();

        // On the way up, so a month of somebody's afternoons isn't carried around for a week
        // waiting for an hour to pass.
        Housekeep(force: true);
    }

    /// <summary>Raised when the list changes, so a window showing it can catch up.</summary>
    public event Action? Changed;

    /// <summary>
    /// Notes something the user did.
    /// </summary>
    /// <remarks>
    /// A run of the same work on the same thing is one line rather than many: typing into a
    /// description sends a write every time the typing pauses, and a list with forty of those in it
    /// is a list nobody reads. The line stays at the time the run started, since that is when the
    /// user would say they did it, and takes the newest wording, since that is what the row in
    /// front of them says.
    ///
    /// A run is kept in memory until it closes, which is what lets the wording change without the
    /// file ever having to be rewritten: what reaches the store is only ever new rows.
    /// </remarks>
    /// <param name="said">The whole of it as a sentence</param>
    /// <param name="key">What makes this the same piece of work as the one before it</param>
    public void Note(string said, string key)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;

            if (_open is not null && _openKey == key && now - _touched < Window)
            {
                _open = _open with { Said = said };
                _touched = now;
            }
            else
            {
                // Doing something else is what closes a run off, so an edit returned to after a
                // detour is a new line and reads as one.
                if (_open is { } closed)
                    _pending.Add(closed);

                _open = new HistoryEntry(now, said);
                _openKey = key;
                _touched = now;
            }

            if (_pending.Count >= MostPending)
                Write();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Writes what is waiting, and does the housekeeping if it is due.
    /// </summary>
    /// <remarks>
    /// Called on the sync loop's own cadence rather than on a clock of its own — there is already
    /// something coming round every three quarters of a minute, and a second timer to write a
    /// handful of rows would be a thread for nothing.
    ///
    /// The open run stays open. Writing it would mean coming back to change it when the next
    /// keystroke lands, and the whole point of holding it is that the file only ever gains rows.
    /// </remarks>
    public void Flush()
    {
        lock (_gate)
        {
            Write();
            Housekeep(force: false);
        }
    }

    /// <summary>What has been done, newest first: the run in hand, then what the file holds.</summary>
    /// <remarks>
    /// Asked for when the viewer opens and not before. Nothing calls this to keep a list up to
    /// date — the window reads it again when it is told something changed.
    /// </remarks>
    public IReadOnlyList<HistoryEntry> Recent()
    {
        lock (_gate)
        {
            var found = new List<HistoryEntry>();

            if (_open is { } open)
                found.Add(open);

            for (var i = _pending.Count - 1; i >= 0; i--)
                found.Add(_pending[i]);

            if (_store is not null)
            {
                foreach (var stored in _store.Recent(MostShown))
                    found.Add(new HistoryEntry(stored.At, stored.Said));
            }

            return found.Count > MostShown ? found[..MostShown] : found;
        }
    }

    /// <summary>Empties it, in memory and on disk, for a user who would rather it didn't say.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _open = null;
            _openKey = null;
            _pending.Clear();
            _store?.Clear();
        }

        Changed?.Invoke();
    }

    /// <summary>Writes everything, open run included, since there will be no next time.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_open is { } open)
            {
                _pending.Add(open);
                _open = null;
                _openKey = null;
            }

            Write();
        }
    }

    /// <summary>Hands what is waiting to the store. Called with the lock held.</summary>
    private void Write()
    {
        if (_pending.Count == 0 || _store is null)
        {
            // With nowhere to put them they would otherwise grow without limit for the life of the
            // window, which is the one way a note-taker can become the problem.
            if (_store is null && _pending.Count > MostShown)
                _pending.RemoveRange(0, _pending.Count - MostShown);

            return;
        }

        try
        {
            _store.Append(_pending.Select(e => new StoredAction(e.At, e.Said)).ToList());
            _pending.Clear();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A history that can't be written is not a reason to stop working. They are dropped
            // rather than kept, because keeping them would grow a list nothing is ever going to
            // drain — the failure would compound into a second, larger one.
            _pending.Clear();
        }
    }

    /// <summary>Takes out what is past keeping. Called with the lock held.</summary>
    private void Housekeep(bool force)
    {
        if (_store is null)
            return;

        var now = _clock.UtcNow;
        if (!force && now - _swept < Sweeping)
            return;

        _swept = now;

        try
        {
            _store.Sweep(now - Keep);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Tidying is not worth an exception reaching the user. It comes round again in an hour.
        }
    }
}
