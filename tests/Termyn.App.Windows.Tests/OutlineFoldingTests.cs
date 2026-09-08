using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// How the outline asks for a task's sub-tasks to be hidden — by the arrow keys, and by the
/// expander at the head of the row.
/// </summary>
/// <remarks>
/// The list asks and never decides: which rows exist is the presenter's, and everything here is
/// about what the control says it wants. So each of these watches the request rather than the rows.
/// </remarks>
public class OutlineFoldingTests
{
    private static TaskRow Row(string id, int depth = 0, bool children = false, bool collapsed = false)
        => new(id, id + " task", Priority.P4, "Work", string.Empty, [], depth, HasChildren: children, Collapsed: collapsed);

    private static OutlineView Outline(params TaskRow[] rows)
    {
        var view = new OutlineView { Theme = Theme.Resolve(ThemePreference.Light) };
        view.CreateControl();
        view.Rows = rows;
        return view;
    }

    /// <summary>What the list asked for while something was done to it, if anything.</summary>
    private static List<(string Id, bool Collapsed)> Asked(OutlineView view, Action change)
    {
        var seen = new List<(string, bool)>();
        void Record(string id, bool collapsed) => seen.Add((id, collapsed));

        view.CollapseRequested += Record;
        try
        {
            change();
        }
        finally
        {
            view.CollapseRequested -= Record;
        }

        return seen;
    }

    /// <summary>
    /// Selects a task, and makes sure the selection went there.
    /// </summary>
    /// <remarks>
    /// Asking is not enough. A selection set on one of these controls sometimes doesn't take — the
    /// same thing that has the markdown panel drawing its runs in the wrong places — and an arrow
    /// key with nothing selected asks for nothing, so a test that carried on would report the fold
    /// not happening when what went wrong was the setting up. Seen here once, on a run that passed
    /// on being asked again.
    /// </remarks>
    private static void Pick(OutlineView view, string id)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            view.SelectId(id);

            if (view.SelectedId == id)
                return;
        }

        Assert.Fail($"'{id}' would not select — the list says {view.SelectedId ?? "nothing"} is selected.");
    }

    /// <summary>WM_KEYDOWN, and a press of one key at the list the way the message loop delivers it.</summary>
    private const int WmKeyDown = 0x0100;

    private static bool Press(OutlineView view, Keys key)
    {
        var message = Message.Create(view.Handle, WmKeyDown, (nint)key, 0);
        return view.PreProcessMessage(ref message);
    }

    // ---- The arrow keys ------------------------------------------------------------------------

    [WinFormsFact]
    public void Left_folds_the_selected_task_and_right_opens_it_again()
    {
        using var view = Outline(Row("a", children: true), Row("b", depth: 1));
        Pick(view, "a");

        Assert.Equal([("a", true)], Asked(view, () => Press(view, Keys.Left)));

        view.Rows = [Row("a", children: true, collapsed: true)];
        Pick(view, "a");

        Assert.Equal([("a", false)], Asked(view, () => Press(view, Keys.Right)));
    }

    [WinFormsFact]
    public void An_arrow_asks_for_nothing_when_it_is_already_that_way()
    {
        // Right on a task already open, and Left on one already folded. Asked for anyway, the list
        // would be rebuilt for no change — which moves the selection and the viewport under
        // whoever is reading it.
        using var open = Outline(Row("a", children: true), Row("b", depth: 1));
        Pick(open, "a");
        Assert.Empty(Asked(open, () => Press(open, Keys.Right)));

        using var folded = Outline(Row("a", children: true, collapsed: true));
        Pick(folded, "a");
        Assert.Empty(Asked(folded, () => Press(folded, Keys.Left)));
    }

    [WinFormsFact]
    public void A_task_with_nothing_under_it_answers_to_neither_arrow()
    {
        using var view = Outline(Row("a"));
        Pick(view, "a");

        Assert.Empty(Asked(view, () => Press(view, Keys.Left)));
        Assert.Empty(Asked(view, () => Press(view, Keys.Right)));
    }

    [WinFormsFact]
    public void An_arrow_with_nothing_selected_is_left_alone()
    {
        using var view = Outline(Row("a", children: true), Row("b", depth: 1));

        Assert.Empty(Asked(view, () => Press(view, Keys.Left)));
    }

    [WinFormsFact]
    public void An_arrow_this_has_no_use_for_is_left_for_something_else()
    {
        // A key swallowed by whatever happens to have the focus is the sort of thing nobody can
        // account for later, so one that folds nothing is not marked handled.
        using var view = Outline(Row("a"));
        Pick(view, "a");

        Assert.False(Press(view, Keys.Left));
        Assert.False(Press(view, Keys.Right));
    }

    // ---- The expander --------------------------------------------------------------------------

    /// <summary>
    /// Where a row's expander sits, worked out the way the drawing places it.
    /// </summary>
    /// <remarks>
    /// The middle of the room it is given, on the row's own line: past the indent its depth earns
    /// it and no further. Aimed at the room rather than at the arrow's own eight pixels, since the
    /// room is what a pointer has to hit.
    /// </remarks>
    private static Point ExpanderOf(OutlineView view, int index, int depth)
    {
        var bounds = view.GetItemRect(index, ItemBoundsPortion.Entire);
        return new Point(bounds.X + (depth * 18) + 7, bounds.Y + (bounds.Height / 2));
    }

    /// <remarks>
    /// That pressing the expander asks for the fold isn't asserted here. Delivering a click to a
    /// native list means standing in for Windows, and what would be held is the three lines that
    /// dispatch it rather than the thing that can be wrong — which is where the expander is. So
    /// these ask the control what is under a point, which is the question the press asks it.
    /// </remarks>
    [WinFormsFact]
    public void The_expander_is_what_is_under_the_head_of_the_row()
    {
        using var view = Outline(Row("a", children: true), Row("b", depth: 1));

        Assert.Equal("a", view.ExpanderAt(ExpanderOf(view, 0, depth: 0)));
    }

    [WinFormsFact]
    public void The_expander_of_an_indented_row_moves_in_with_it()
    {
        // It sits at the head of the row's own words, so a sub-task's expander is where the
        // sub-task starts and not where its parent does.
        using var view = Outline(Row("a", children: true), Row("b", depth: 1, children: true), Row("c", depth: 2));

        Assert.Equal("b", view.ExpanderAt(ExpanderOf(view, 1, depth: 1)));

        // The parent's own place on that row is a click on the row, not on its child's expander.
        Assert.Null(view.ExpanderAt(ExpanderOf(view, 1, depth: 0)));
    }

    [WinFormsFact]
    public void A_row_with_nothing_under_it_has_no_expander_to_hit()
    {
        using var view = Outline(Row("a"));

        Assert.Null(view.ExpanderAt(ExpanderOf(view, 0, depth: 0)));
    }

    [WinFormsFact]
    public void The_words_are_not_the_expander()
    {
        // Selecting a task has to stay something that can be done to one with sub-tasks under it.
        using var view = Outline(Row("a", children: true), Row("b", depth: 1));
        var bounds = view.GetItemRect(0, ItemBoundsPortion.Entire);

        Assert.Null(view.ExpanderAt(new Point(bounds.X + 80, bounds.Y + (bounds.Height / 2))));
    }

    [WinFormsFact]
    public void A_point_past_the_last_row_is_not_an_expander()
    {
        // A list is nearly always taller than the tasks in it, and the room below them belongs to
        // no row at all.
        using var view = Outline(Row("a", children: true), Row("b", depth: 1));
        var bounds = view.GetItemRect(1, ItemBoundsPortion.Entire);

        Assert.Null(view.ExpanderAt(new Point(bounds.X + 7, bounds.Bottom + (bounds.Height * 3))));
    }
}
