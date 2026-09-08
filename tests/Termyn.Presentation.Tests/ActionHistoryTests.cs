using Termyn.Core.History;
using Termyn.Core.Platform;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The list of what has been done, and the rule that keeps it worth reading.
/// </summary>
/// <remarks>
/// The whole difficulty is noise. A description sends a write every time the typing pauses, so the
/// question this answers is not "was it recorded" but "how many lines did an afternoon leave".
/// </remarks>
public class ActionHistoryTests
{
    /// <summary>A clock that can be pushed forward, since the rule here is about elapsed time.</summary>
    private sealed class Moving : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

        public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

        public void Pass(TimeSpan time) => UtcNow += time;
    }

    private static string[] Said(ActionHistory history) => history.Recent().Select(e => e.Said).ToArray();

    // ---- Not making a fuss ---------------------------------------------------------------------

    [Fact]
    public void A_run_of_edits_to_one_thing_is_one_line()
    {
        // The case that prompted the rule: ten minutes on a description, a write every time the
        // typing paused. Forty lines saying the same thing is a list nobody opens twice.
        var clock = new Moving();
        var history = new ActionHistory(null, clock);

        for (var i = 0; i < 40; i++)
        {
            history.Note("Edited the description of “Plan the week”", "description:Task:t1");
            clock.Pass(TimeSpan.FromSeconds(15));
        }

        Assert.Equal(["Edited the description of “Plan the week”"], Said(history));
    }

    [Fact]
    public void A_run_keeps_the_time_it_started()
    {
        // When you would say you did it. The last keystroke of a long edit is not the moment the
        // work happened, and a list ordered by it would drift away from what the user remembers.
        var clock = new Moving();
        var started = clock.UtcNow;
        var history = new ActionHistory(null, clock);

        history.Note("Edited the description of “Plan the week”", "description:Task:t1");
        clock.Pass(TimeSpan.FromMinutes(4));
        history.Note("Edited the description of “Plan the week”", "description:Task:t1");

        Assert.Equal(started, Assert.Single(history.Recent()).At);
    }

    [Fact]
    public void A_run_that_is_left_alone_for_long_enough_is_over()
    {
        // Coming back to something after a while is doing it again, and reads better as two lines
        // than as one that claims to have taken all morning.
        var clock = new Moving();
        var history = new ActionHistory(null, clock);

        history.Note("Edited the description of “Plan the week”", "description:Task:t1");
        clock.Pass(TimeSpan.FromMinutes(30));
        history.Note("Edited the description of “Plan the week”", "description:Task:t1");

        Assert.Equal(2, history.Recent().Count);
    }

    [Fact]
    public void Doing_something_else_in_between_closes_the_run_off()
    {
        // The window alone isn't enough. Editing, going away to complete something, and coming back
        // is three things, and the middle one would be swallowed by a rule that only watched time.
        var clock = new Moving();
        var history = new ActionHistory(null, clock);

        history.Note("Edited the description of “Plan the week”", "description:Task:t1");
        history.Note("Completed “Tidy the garage”", "complete:t2");
        history.Note("Edited the description of “Plan the week”", "description:Task:t1");

        Assert.Equal(3, history.Recent().Count);
    }

    [Fact]
    public void The_same_kind_of_work_on_another_thing_is_its_own_line()
    {
        var clock = new Moving();
        var history = new ActionHistory(null, clock);

        history.Note("Edited the description of “Plan the week”", "description:Task:t1");
        history.Note("Edited the description of “Tidy the garage”", "description:Task:t2");

        Assert.Equal(2, history.Recent().Count);
    }

    [Fact]
    public void A_run_says_where_it_got_to_rather_than_where_it_started()
    {
        // Renaming twice inside a run is one rename, and the name it should show is the one the
        // task has now — the row in front of the user says the same thing.
        var clock = new Moving();
        var history = new ActionHistory(null, clock);

        history.Note("Renamed “a” to “b”", "rename:t1");
        history.Note("Renamed “a” to “c”", "rename:t1");

        Assert.Equal(["Renamed “a” to “c”"], Said(history));
    }

    // ---- The shape of the list -----------------------------------------------------------------

    [Fact]
    public void The_newest_is_at_the_top()
    {
        var history = new ActionHistory(null, new Moving());

        history.Note("Added “one”", "a:1");
        history.Note("Added “two”", "a:2");

        Assert.Equal(["Added “two”", "Added “one”"], Said(history));
    }

    // ---- What is held, and what is written -----------------------------------------------------

    /// <summary>A store that says what it was asked to do, rather than doing it to a file.</summary>
    private sealed class Recording : IHistoryStore
    {
        public List<StoredAction> Held { get; } = [];

        public List<DateTimeOffset> Sweeps { get; } = [];

        public int Batches { get; private set; }

        public void Append(IReadOnlyList<StoredAction> entries)
        {
            Batches++;
            Held.InsertRange(0, entries.Reverse());   // newest first, the way Recent hands them back
        }

        public IReadOnlyList<StoredAction> Recent(int limit) => Held.Take(limit).ToList();

        public int Sweep(DateTimeOffset before)
        {
            Sweeps.Add(before);
            return Held.RemoveAll(e => e.At < before);
        }

        public void Clear() => Held.Clear();

        public void Dispose() { }
    }

    [Fact]
    public void The_run_in_hand_is_not_written_until_it_closes()
    {
        // What lets the wording change without the file ever being rewritten: only closed runs go,
        // so the store only ever gains rows.
        var store = new Recording();
        var history = new ActionHistory(store, new Moving());

        history.Note("Edited the description of a task", "description:t1");
        history.Flush();

        Assert.Empty(store.Held);
        Assert.Single(history.Recent());
    }

    [Fact]
    public void What_is_behind_the_run_in_hand_is_written()
    {
        var store = new Recording();
        var history = new ActionHistory(store, new Moving());

        history.Note("Completed one", "c:1");
        history.Note("Completed two", "c:2");   // which closes the first
        history.Flush();

        Assert.Equal(["Completed one"], store.Held.Select(e => e.Said).ToArray());
    }

    [Fact]
    public void Nothing_is_written_a_row_at_a_time()
    {
        // The whole reason they are saved up: a disk sync per pause in the typing is the cost this
        // is arranged to avoid.
        var store = new Recording();
        var history = new ActionHistory(store, new Moving());

        for (var i = 0; i < 10; i++)
            history.Note($"Completed {i}", $"c:{i}");

        history.Flush();

        Assert.Equal(1, store.Batches);
    }

    [Fact]
    public void Enough_waiting_writes_without_being_asked()
    {
        // A backstop for somebody working faster than the sync loop comes round, so what a sudden
        // end can lose stays small.
        var store = new Recording();
        var history = new ActionHistory(store, new Moving());

        for (var i = 0; i < 40; i++)
            history.Note($"Completed {i}", $"c:{i}");

        Assert.NotEmpty(store.Held);
    }

    [Fact]
    public void Closing_writes_the_run_that_was_still_open()
    {
        // There is no next time, so the one being held back goes with the rest.
        var store = new Recording();
        var history = new ActionHistory(store, new Moving());
        history.Note("Edited the description of a task", "description:t1");

        history.Dispose();

        Assert.Equal(["Edited the description of a task"], store.Held.Select(e => e.Said).ToArray());
    }

    [Fact]
    public void What_is_shown_is_memory_and_then_the_file()
    {
        // Newest first across both, so the join doesn't show as a jump in the list.
        var store = new Recording();
        var history = new ActionHistory(store, new Moving());

        history.Note("Completed one", "c:1");
        history.Note("Completed two", "c:2");
        history.Flush();                        // one goes to the store, two stays open
        history.Note("Completed three", "c:3"); // which closes two

        Assert.Equal(
            ["Completed three", "Completed two", "Completed one"],
            history.Recent().Select(e => e.Said).ToArray());
    }

    /// <summary>Counts how often it was read from.</summary>
    private sealed class Reading : IHistoryStore
    {
        public int Reads { get; private set; }

        public void Append(IReadOnlyList<StoredAction> entries) { }

        public IReadOnlyList<StoredAction> Recent(int limit)
        {
            Reads++;
            return [];
        }

        public int Sweep(DateTimeOffset before) => 0;

        public void Clear() { }

        public void Dispose() { }
    }

    [Fact]
    public void The_file_is_not_read_until_somebody_asks_to_see_it()
    {
        // A month of afternoons has no business sitting in a process that mostly wants to be small,
        // so noting something never reads anything back.
        var store = new Reading();
        var history = new ActionHistory(store, new Moving());

        history.Note("Completed one", "c:1");
        history.Note("Completed two", "c:2");
        history.Flush();

        Assert.Equal(0, store.Reads);

        history.Recent();

        Assert.Equal(1, store.Reads);
    }

    // ---- Housekeeping --------------------------------------------------------------------------

    [Fact]
    public void It_tidies_on_the_way_up()
    {
        // Or a month of somebody's afternoons is carried around for a week waiting for an hour.
        var clock = new Moving();
        var store = new Recording();

        _ = new ActionHistory(store, clock);

        Assert.Equal(clock.UtcNow - TimeSpan.FromDays(31), Assert.Single(store.Sweeps));
    }

    [Fact]
    public void It_does_not_tidy_on_every_flush()
    {
        // A month is a long time to be wrong about, so there is no hurry at all — and the flush
        // itself comes round every three quarters of a minute.
        var clock = new Moving();
        var store = new Recording();
        var history = new ActionHistory(store, clock);

        // A working day of them.
        for (var i = 0; i < 60; i++)
        {
            history.Flush();
            clock.Pass(TimeSpan.FromMinutes(8));
        }

        // Just the one on the way up: eight hours of flushing is not a day.
        Assert.Equal(1, store.Sweeps.Count);

        // And a day later it comes round, without having been asked any differently.
        clock.Pass(TimeSpan.FromDays(1));
        history.Flush();

        Assert.Equal(2, store.Sweeps.Count);
    }

    [Fact]
    public void What_it_tidies_away_is_a_month_old()
    {
        var clock = new Moving();
        var store = new Recording();
        store.Held.Add(new StoredAction(clock.UtcNow - TimeSpan.FromDays(40), "Ancient"));
        store.Held.Add(new StoredAction(clock.UtcNow - TimeSpan.FromDays(3), "Recent"));

        _ = new ActionHistory(store, clock);

        Assert.Equal(["Recent"], store.Held.Select(e => e.Said).ToArray());
    }


    [Fact]
    public void It_says_when_it_has_changed()
    {
        // What a window showing it listens to.
        var history = new ActionHistory(null, new Moving());
        var told = 0;
        history.Changed += () => told++;

        history.Note("Added “one”", "a:1");
        history.Note("Added “one”", "a:1");   // folded into the line above, and still a change

        Assert.Equal(2, told);
    }

    [Fact]
    public void Clearing_it_empties_it_and_says_so()
    {
        var history = new ActionHistory(null, new Moving());
        history.Note("Added “one”", "a:1");

        var told = 0;
        history.Changed += () => told++;

        history.Clear();

        Assert.Empty(history.Recent());
        Assert.Equal(1, told);
    }
}
