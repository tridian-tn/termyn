using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The description box's undo, which is ours because the control's own can't be used once the text
/// is highlighted. Typed at in the plain: no window, no control, just what it said and what comes
/// back.
/// </summary>
public class DescriptionHistoryTests
{
    /// <summary>Types a series of pauses into a fresh history, as the idle timer would.</summary>
    private static DescriptionHistory After(string opened, params string[] pauses)
    {
        var history = new DescriptionHistory();
        history.Reset(opened);

        foreach (var text in pauses)
            history.Record(text, text.Length);

        return history;
    }

    [Fact]
    public void A_freshly_opened_description_has_nothing_behind_it()
    {
        // The one that matters most: without it, Ctrl+Z on a task you have just clicked replaces
        // its description with the previous task's — an edit nobody made, saved on the next pause.
        var history = After("the description of this task");

        Assert.False(history.CanUndo);
        Assert.Null(history.Undo("the description of this task", 0));
    }

    [Fact]
    public void Undo_gives_back_what_was_there_before_the_last_pause()
    {
        var history = After("one", "one two", "one two three");

        var undone = history.Undo("one two three", 13);

        Assert.Equal("one two", undone?.Text);
    }

    [Fact]
    public void Undo_walks_all_the_way_back_to_what_was_opened_and_stops()
    {
        var history = After("one", "one two", "one two three");

        Assert.Equal("one two", history.Undo("one two three", 13)?.Text);
        Assert.Equal("one", history.Undo("one two", 7)?.Text);
        Assert.Null(history.Undo("one", 3));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Redo_puts_back_what_undo_took()
    {
        var history = After("one", "one two");
        history.Undo("one two", 7);

        Assert.True(history.CanRedo);
        Assert.Equal("one two", history.Redo()?.Text);
        Assert.Null(history.Redo());
    }

    [Fact]
    public void The_caret_comes_back_where_the_edit_was()
    {
        // Undoing to the top of a long description and leaving the caret at the bottom of it is
        // the kind of thing that makes an undo feel broken even when the text is right.
        var history = new DescriptionHistory();
        history.Reset("a description with some length to it");
        history.Record("a description with MORE length to it", 23);
        history.Record("a description with MORE length to it, and more", 46);

        var undone = history.Undo("a description with MORE length to it, and more", 46);

        Assert.Equal(23, undone?.Caret);
    }

    [Fact]
    public void Typing_mid_sentence_is_not_lost_by_pressing_undo_before_the_pause()
    {
        // Ctrl+Z pressed between two keystrokes, before the idle tick that would have recorded the
        // sentence. Without noting what is on screen first, the sentence is thrown away rather
        // than undone — and redo has nothing to put back.
        var history = After("one", "one two");

        var undone = history.Undo("one two three, still being typed", 32);

        Assert.Equal("one two", undone?.Text);
        Assert.Equal("one two three, still being typed", history.Redo()?.Text);
    }

    [Fact]
    public void Recording_the_same_words_twice_is_not_two_states()
    {
        // The idle timer fires on a pause whether or not anything changed — moving the caret is
        // enough. Each of those must not become something to undo.
        var history = After("one", "one two", "one two", "one two");

        Assert.Equal("one", history.Undo("one two", 7)?.Text);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void The_caret_still_moves_when_the_words_do_not()
    {
        var history = After("one two");
        history.Record("one two", 7);
        history.Record("one two", 3);
        history.Record("one two three", 13);

        Assert.Equal(3, history.Undo("one two three", 13)?.Caret);
    }

    [Fact]
    public void Typing_after_an_undo_drops_what_was_undone()
    {
        // Otherwise redo produces text nobody ever wrote — the tail of an abandoned branch,
        // reappearing over what was typed in its place.
        var history = After("one", "one two", "one two three");
        history.Undo("one two three", 13);

        history.Record("one two, then something else", 28);

        Assert.False(history.CanRedo);
        Assert.Equal("one two", history.Undo("one two, then something else", 28)?.Text);
    }

    [Fact]
    public void Recording_onto_a_history_that_was_never_opened_still_works()
    {
        // Reset comes from the box being filled, and Record from the typing. They arrive in that
        // order every time except the once that matters.
        var history = new DescriptionHistory();

        history.Record("typed into a box nobody opened", 30);

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Opening_another_task_forgets_the_one_before_it()
    {
        var history = After("first task", "first task, edited");

        history.Reset("second task");

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.Undo("second task", 0));
    }

    // ---- Not growing without limit -------------------------------------------------------------

    [Fact]
    public void A_long_session_of_edits_keeps_only_so_many()
    {
        var history = new DescriptionHistory();
        history.Reset("0");

        for (var i = 1; i <= 500; i++)
            history.Record(i.ToString(), 1);

        // Still undoes, and still stops rather than running out of a list it has walked off.
        var steps = 0;
        while (history.Undo(history.Undo("500", 3)?.Text ?? "500", 3) is not null && steps < 1000)
            steps++;

        Assert.True(steps < 1000, "undo did not come to a stop");
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void A_run_of_full_length_descriptions_does_not_hold_them_all()
    {
        // Sixteen thousand characters is what a description can hold, and a hundred of those would
        // be well over a megabyte kept against a box the user has walked away from.
        var big = new string('x', 16_383);
        var history = new DescriptionHistory();
        history.Reset(big);

        for (var i = 0; i < 100; i++)
            history.Record(big + i, 16_383);

        var depth = 0;
        var text = big + 99;
        while (history.Undo(text, 0) is { } state)
        {
            text = state.Text;
            depth++;
        }

        // Deep enough to be useful, shallow enough not to be a leak: the character ceiling is what
        // stops it here rather than the count.
        Assert.InRange(depth, 5, 40);
    }
}
