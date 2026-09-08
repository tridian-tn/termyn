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

    private static string[] Said(ActionHistory history) => history.Entries.Select(e => e.Said).ToArray();

    // ---- Not making a fuss ---------------------------------------------------------------------

    [Fact]
    public void A_run_of_edits_to_one_thing_is_one_line()
    {
        // The case that prompted the rule: ten minutes on a description, a write every time the
        // typing paused. Forty lines saying the same thing is a list nobody opens twice.
        var clock = new Moving();
        var history = new ActionHistory(clock);

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
        var history = new ActionHistory(clock);

        history.Note("Edited the description of “Plan the week”", "description:Task:t1");
        clock.Pass(TimeSpan.FromMinutes(4));
        history.Note("Edited the description of “Plan the week”", "description:Task:t1");

        Assert.Equal(started, Assert.Single(history.Entries).At);
    }

    [Fact]
    public void A_run_that_is_left_alone_for_long_enough_is_over()
    {
        // Coming back to something after a while is doing it again, and reads better as two lines
        // than as one that claims to have taken all morning.
        var clock = new Moving();
        var history = new ActionHistory(clock);

        history.Note("Edited the description of “Plan the week”", "description:Task:t1");
        clock.Pass(TimeSpan.FromMinutes(30));
        history.Note("Edited the description of “Plan the week”", "description:Task:t1");

        Assert.Equal(2, history.Entries.Count);
    }

    [Fact]
    public void Doing_something_else_in_between_closes_the_run_off()
    {
        // The window alone isn't enough. Editing, going away to complete something, and coming back
        // is three things, and the middle one would be swallowed by a rule that only watched time.
        var clock = new Moving();
        var history = new ActionHistory(clock);

        history.Note("Edited the description of “Plan the week”", "description:Task:t1");
        history.Note("Completed “Tidy the garage”", "complete:t2");
        history.Note("Edited the description of “Plan the week”", "description:Task:t1");

        Assert.Equal(3, history.Entries.Count);
    }

    [Fact]
    public void The_same_kind_of_work_on_another_thing_is_its_own_line()
    {
        var clock = new Moving();
        var history = new ActionHistory(clock);

        history.Note("Edited the description of “Plan the week”", "description:Task:t1");
        history.Note("Edited the description of “Tidy the garage”", "description:Task:t2");

        Assert.Equal(2, history.Entries.Count);
    }

    [Fact]
    public void A_run_says_where_it_got_to_rather_than_where_it_started()
    {
        // Renaming twice inside a run is one rename, and the name it should show is the one the
        // task has now — the row in front of the user says the same thing.
        var clock = new Moving();
        var history = new ActionHistory(clock);

        history.Note("Renamed “a” to “b”", "rename:t1");
        history.Note("Renamed “a” to “c”", "rename:t1");

        Assert.Equal(["Renamed “a” to “c”"], Said(history));
    }

    // ---- The shape of the list -----------------------------------------------------------------

    [Fact]
    public void The_newest_is_at_the_top()
    {
        var history = new ActionHistory(new Moving());

        history.Note("Added “one”", "a:1");
        history.Note("Added “two”", "a:2");

        Assert.Equal(["Added “two”", "Added “one”"], Said(history));
    }

    [Fact]
    public void It_does_not_grow_for_ever()
    {
        // A note of what you have been doing rather than an archive, so the oldest goes.
        var history = new ActionHistory(new Moving());

        for (var i = 0; i < 250; i++)
            history.Note($"Added “{i}”", $"a:{i}");

        Assert.Equal(200, history.Entries.Count);
        Assert.Equal("Added “249”", history.Entries[0].Said);
        Assert.Equal("Added “50”", history.Entries[^1].Said);
    }

    [Fact]
    public void It_says_when_it_has_changed()
    {
        // What a window showing it listens to.
        var history = new ActionHistory(new Moving());
        var told = 0;
        history.Changed += () => told++;

        history.Note("Added “one”", "a:1");
        history.Note("Added “one”", "a:1");   // folded into the line above, and still a change

        Assert.Equal(2, told);
    }

    [Fact]
    public void Clearing_it_empties_it_and_says_so()
    {
        var history = new ActionHistory(new Moving());
        history.Note("Added “one”", "a:1");

        var told = 0;
        history.Changed += () => told++;

        history.Clear();

        Assert.Empty(history.Entries);
        Assert.Equal(1, told);

        // And again is nothing to say, since nothing changed.
        history.Clear();
        Assert.Equal(1, told);
    }
}
