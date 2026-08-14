using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The comments pane. Realised without ever being shown, as the other control tests are: the list
/// measures its rows against its own width, so it needs a window behind it to have one.
/// </summary>
public class CommentsViewTests
{
    private static CommentsView Realised(params CommentRow[] comments)
    {
        var view = new CommentsView { Theme = Theme.Resolve(ThemePreference.Light) };
        view.CreateControl();
        view.Size = new Size(420, 260);
        view.CanComment = true;
        view.Comments = comments;
        return view;
    }

    /// <summary>Puts the list on a row, standing in for the user having clicked or arrowed to it.</summary>
    private static void Select(CommentsView view, int index)
        => view.Controls.OfType<ListBox>().Single().SelectedIndex = index;

    private static CommentRow Comment(string id, string content, string posted = "12 Aug 2026, 09:30")
        => new(id, content, posted, null);

    // ---- What it shows --------------------------------------------------------------------------

    [Fact]
    public void The_last_comment_is_the_one_it_lands_on()
    {
        // The newest is the one being replied to. Landing on the oldest would mean scrolling past
        // the whole conversation every time the pane opens.
        using var view = Realised(Comment("n1", "first"), Comment("n2", "second"), Comment("n3", "third"));

        Assert.Equal("n3", view.SelectedId);
    }

    [Fact]
    public void Nothing_is_selected_when_there_is_nothing_to_select()
    {
        using var view = Realised();

        Assert.Null(view.SelectedId);
    }

    [Fact]
    public void Sitting_on_the_newest_it_follows_the_conversation_on()
    {
        // Which is what makes posting one visible: the pane lands on the newest, so the comment you
        // just wrote is the one in view rather than somewhere below the fold.
        using var view = Realised(Comment("n1", "first"), Comment("n2", "second"));

        view.Comments = [Comment("n1", "first"), Comment("n2", "second"), Comment("n3", "third")];

        Assert.Equal("n3", view.SelectedId);
    }

    [Fact]
    public void Having_gone_back_up_the_conversation_it_stays_where_it_was_put()
    {
        // A sync republishes these every forty-five seconds. Reading an older comment and having the
        // selection jump to the bottom each time would take the delete key with it.
        using var view = Realised(Comment("n1", "first"), Comment("n2", "second"), Comment("n3", "third"));
        Select(view, index: 0);

        view.Comments = [Comment("n1", "first"), Comment("n2", "second"), Comment("n3", "third")];
        Assert.Equal("n1", view.SelectedId);

        view.Comments = [Comment("n1", "first"), Comment("n2", "second"), Comment("n3", "third"), Comment("n4", "fourth")];
        Assert.Equal("n1", view.SelectedId);
    }

    [Fact]
    public void A_comment_that_has_gone_does_not_leave_the_selection_on_it()
    {
        using var view = Realised(Comment("n1", "first"), Comment("n2", "second"));

        view.Comments = [Comment("n1", "first")];

        Assert.Equal("n1", view.SelectedId);
    }

    // ---- The box ---------------------------------------------------------------------------------

    [Fact]
    public void The_box_is_there_when_a_comment_can_be_added_and_gone_when_it_cannot()
    {
        using var view = Realised(Comment("n1", "first"));
        Assert.True(view.CanComment);

        view.CanComment = false;
        Assert.False(view.CanComment);

        view.CanComment = true;
        Assert.True(view.CanComment);
    }

    [Fact]
    public void Turning_the_box_on_gives_it_a_height_to_be_seen_in()
    {
        // It starts hidden precisely so that the first time it is turned on counts as a change. Left
        // visible from the outset, the set that turns it on would do nothing and the box would be
        // there at no height at all.
        using var view = new CommentsView { Theme = Theme.Resolve(ThemePreference.Light) };
        view.CreateControl();
        view.Size = new Size(420, 260);

        view.CanComment = true;

        var box = view.Controls.OfType<Panel>().Single();
        Assert.True(box.Height > 0, $"the compose area is {box.Height}px tall, so nothing can be typed into it");
    }

    // ---- What it says when there is nothing ------------------------------------------------------

    [Fact]
    public void An_empty_conversation_says_so_where_the_list_would_have_been()
    {
        using var view = Realised();
        view.Placeholder = "No comments yet.";

        var empty = view.Controls.OfType<Label>().Single(l => l.Text == "No comments yet.");
        Assert.True(empty.Visible, "the empty-state line is hidden, so the pane draws as blank");
    }

    [Fact]
    public void The_line_goes_once_there_is_something_to_read()
    {
        using var view = Realised();
        view.Placeholder = "No comments yet.";

        view.Comments = [Comment("n1", "something")];

        var empty = view.Controls.OfType<Label>().Single(l => l.Text == "No comments yet.");
        Assert.False(empty.Visible);
    }

    // ---- Height ----------------------------------------------------------------------------------

    [Fact]
    public void A_comment_of_many_lines_is_given_more_room_than_one_of_a_few_words()
    {
        // A fixed row height would either clip most comments or waste a short panel on the brief
        // ones, which is why the rows are measured at all.
        using var view = Realised(
            Comment("n1", "short"),
            Comment("n2", string.Join(" ", Enumerable.Repeat("a rather longer comment that has to wrap", 12))));

        var list = view.Controls.OfType<ListBox>().Single();

        Assert.True(
            list.GetItemHeight(1) > list.GetItemHeight(0),
            $"the long comment is {list.GetItemHeight(1)}px and the short one {list.GetItemHeight(0)}px");
    }
}
