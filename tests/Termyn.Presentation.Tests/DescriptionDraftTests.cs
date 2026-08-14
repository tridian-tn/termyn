using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The rules the notes box saves by. Both ways of getting these wrong are quiet: a box that writes
/// when nothing was typed pushes a stale copy back over an edit made on the web, and one that
/// thinks it is clean when it isn't loses what was written.
/// </summary>
public class DescriptionDraftTests
{
    private static DescriptionDraft On(string task, string description)
    {
        var draft = new DescriptionDraft();
        draft.Open(task, description);
        return draft;
    }

    [Fact]
    public void A_box_nobody_has_touched_has_nothing_to_write()
    {
        var draft = On("t1", "As it was");

        Assert.False(draft.IsDirty("As it was"));
        Assert.Null(draft.Take("As it was"));
    }

    [Fact]
    public void A_box_that_was_typed_into_writes_what_is_in_it()
    {
        var draft = On("t1", "As it was");

        Assert.True(draft.IsDirty("As it is now"));
        Assert.Equal(("t1", "As it is now"), draft.Take("As it is now"));
    }

    [Fact]
    public void Emptying_the_notes_is_an_edit_like_any_other()
    {
        // Not a no-op dressed up as one: clearing a description is a thing people do, and a save
        // that skipped it would leave the old notes on the task for ever.
        var draft = On("t1", "Something");

        Assert.Equal(("t1", ""), draft.Take(string.Empty));
    }

    [Fact]
    public void A_box_on_no_task_never_writes()
    {
        var draft = new DescriptionDraft();
        draft.Open(null, string.Empty);

        Assert.False(draft.IsDirty("typed into nothing"));
        Assert.Null(draft.Take("typed into nothing"));
    }

    [Fact]
    public void Taking_the_edit_leaves_the_box_clean()
    {
        // The focus leaving the box and the box then closing are two saves of one edit, and the
        // second would queue a command that changes nothing.
        var draft = On("t1", "As it was");

        Assert.NotNull(draft.Take("Rewritten"));
        Assert.Null(draft.Take("Rewritten"));
        Assert.False(draft.IsDirty("Rewritten"));
    }

    [Fact]
    public void What_changed_is_measured_against_what_the_box_was_opened_with()
    {
        // A sync can rewrite this description while the box sits open. Closing the box untouched
        // must not push what it was opened with back over it — which is what comparing against the
        // account's current copy would do.
        var draft = On("t1", "Opened with this");

        Assert.Null(draft.Take("Opened with this"));
    }

    [Fact]
    public void Moving_to_another_task_measures_against_that_task()
    {
        var draft = On("t1", "First task");

        draft.Open("t2", "Second task");

        Assert.Equal("t2", draft.TaskId);
        Assert.Null(draft.Take("Second task"));
        Assert.Equal(("t2", "Edited"), draft.Take("Edited"));
    }

    [Fact]
    public void A_refresh_is_allowed_only_while_nothing_is_half_typed()
    {
        // The sync loop republishes every forty-five seconds and must not land on top of a
        // sentence in progress.
        var draft = On("t1", "As it was");

        Assert.True(draft.CanRefresh("As it was"));
        Assert.False(draft.CanRefresh("As it was, and a half-written th"));
    }

    [Fact]
    public void An_empty_box_on_no_task_can_always_be_refreshed()
        => Assert.True(new DescriptionDraft().CanRefresh("anything at all"));

    // ---- Line endings --------------------------------------------------------------------------

    /// <summary>As the account keeps it.</summary>
    private const string Bare = "First line\nSecond line";

    /// <summary>As a Windows text box hands it back.</summary>
    private const string Paired = "First line\r\nSecond line";

    [Fact]
    public void A_text_box_handing_back_paired_newlines_is_not_an_edit()
    {
        // Compared as they come, every description would look edited the instant it was shown —
        // and saving that would put a carriage return into the account for good.
        var draft = On("t1", Bare);

        Assert.False(draft.IsDirty(Paired));
        Assert.Null(draft.Take(Paired));
        Assert.True(draft.CanRefresh(Paired));
    }

    [Fact]
    public void What_gets_saved_has_the_line_endings_the_account_keeps()
    {
        var draft = On("t1", "One line");

        var edit = draft.Take("One line\r\nand another");

        Assert.Equal(("t1", "One line\nand another"), edit);
        Assert.DoesNotContain('\r', edit!.Value.Text);
    }

    [Fact]
    public void A_real_edit_is_still_seen_through_the_line_endings()
    {
        // The normalising must not be so keen that it swallows the change it was meant to see past.
        var draft = On("t1", Bare);

        Assert.True(draft.IsDirty(Paired + "\r\nThird"));
    }

    // ---- A task the server renames while the box is open on it -----------------------------------

    [Fact]
    public void Following_a_rename_keeps_what_is_being_typed()
    {
        // A task created a moment ago is renamed when the sync learns what the server calls it, and
        // the box may be part-way through a sentence at the time. Reopening on the new name would
        // replace what is being typed with what the account holds — which for a task that new is
        // nothing at all.
        var draft = new DescriptionDraft();
        draft.Open("t-abc", "opened with this");

        draft.Retarget("9001");

        Assert.Equal("9001", draft.TaskId);
        Assert.Equal("opened with this", draft.Opened);
        Assert.True(draft.IsDirty("and this was typed"));
        Assert.Equal(("9001", "and this was typed"), draft.Take("and this was typed"));
    }

    [Fact]
    public void A_rename_does_not_put_the_box_on_a_task_it_was_never_on()
    {
        // With the box on nothing, a rename arriving from anywhere must not give it something to
        // save to — there is no text of anybody's to save.
        var draft = new DescriptionDraft();
        draft.Open(null, string.Empty);

        draft.Retarget("9001");

        Assert.Null(draft.TaskId);
        Assert.Null(draft.Take("typed into a box on no task"));
    }
}
