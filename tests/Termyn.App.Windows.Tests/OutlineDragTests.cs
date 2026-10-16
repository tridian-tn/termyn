using System.Runtime.InteropServices;
using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// Which drop the list works out from where the pointer is during a drag.
/// </summary>
/// <remarks>
/// Asked of the list directly rather than by dragging: a drag runs a loop of its own with the real
/// mouse, which a test can't drive without taking the pointer away from whoever's at the machine.
/// What's held here is the list's half — which row is under the pointer, wherever across it, and
/// that the presenter is asked once per drop rather than once per move.
/// </remarks>
public class OutlineDragTests
{
    private static TaskRow Task(string id, int depth = 0)
        => new(id, id.ToUpperInvariant(), Priority.P4, "Work", string.Empty, [], Depth: depth);

    private static Form Window()
        => new() { StartPosition = FormStartPosition.Manual, Location = new Point(-2200, -2200), Size = new Size(700, 400) };

    /// <summary>
    /// A list holding a parent with a sub-task and then a second top-level task, with its columns
    /// narrow enough to leave room past the last of them.
    /// </summary>
    private static OutlineView Outline(Form form, Func<string, DropTarget, bool> canDrop)
    {
        var outline = new OutlineView { Dock = DockStyle.Fill, CanDrop = canDrop };
        form.Controls.Add(outline);
        form.Show();

        foreach (ColumnHeader column in outline.Columns)
            column.Width = 60;

        outline.Rows = [Task("a"), Task("b", depth: 1), Task("c")];
        return outline;
    }

    /// <summary>A point a given way down a row, inside the task column.</summary>
    private static Point In(OutlineView outline, int index, double down)
    {
        var row = outline.GetItemRect(index);
        return new Point(row.Left + 30, row.Top + (int)(row.Height * down));
    }

    [WinFormsFact]
    public void The_top_middle_and_bottom_of_a_row_ask_for_before_onto_and_after()
    {
        using var form = Window();
        using var outline = Outline(form, (_, _) => true);

        Assert.Equal(new DropTarget("a", DropZone.Before, 0, 0), outline.DropAt("c", In(outline, 0, 0.1)));
        Assert.Equal(new DropTarget("a", DropZone.Onto, 0, 0), outline.DropAt("c", In(outline, 0, 0.5)));
        Assert.Equal(new DropTarget("b", DropZone.After, 1, 1), outline.DropAt("c", In(outline, 1, 0.9)));
    }

    [WinFormsFact]
    public void Past_the_last_column_the_row_is_still_found()
    {
        // The list's own hit test says there's no row out there, though the row is drawn right across.
        using var form = Window();
        using var outline = Outline(form, (_, _) => true);

        var inside = In(outline, 2, 0.5);
        var past = inside with { X = (60 * outline.Columns.Count) + 100 };

        Assert.True(past.X < outline.ClientSize.Width);
        Assert.Null(outline.HitTest(past).Item);
        Assert.Equal(new DropTarget("c", DropZone.Onto, 2, 0), outline.DropAt("a", past));
    }

    [WinFormsFact]
    public void Scrolled_sideways_the_row_is_still_found()
    {
        // The task column then starts off to the left of the list, and the list won't say what's
        // under a point outside itself.
        using var form = Window();
        using var outline = Outline(form, (_, _) => true);

        foreach (ColumnHeader column in outline.Columns)
            column.Width = 300;

        SendMessage(outline.Handle, LvmScroll, 200, 0);
        Assert.True(outline.GetItemRect(0).Left < 0);

        var row = outline.GetItemRect(2);
        var middle = new Point(100, row.Top + (row.Height / 2));

        Assert.Equal(new DropTarget("c", DropZone.Onto, 2, 0), outline.DropAt("a", middle));
    }

    [WinFormsFact]
    public void Below_the_last_row_asks_for_the_end_of_the_list()
    {
        using var form = Window();
        using var outline = Outline(form, (_, _) => true);

        var below = new Point(30, outline.GetItemRect(2).Bottom + 20);

        Assert.Equal(new DropTarget("c", DropZone.After, 2, 0), outline.DropAt("a", below));
    }

    [WinFormsFact]
    public void A_drop_the_presenter_turns_away_has_nowhere_to_land()
    {
        using var form = Window();
        using var outline = Outline(form, (_, _) => false);

        Assert.Null(outline.DropAt("c", In(outline, 0, 0.5)));
    }

    [WinFormsFact]
    public void The_presenter_is_asked_once_while_the_pointer_stays_over_the_same_drop()
    {
        // Every move of the pointer asks, and the answer walks the account, so a drag across one
        // row's middle mustn't ask the same question dozens of times.
        var asked = 0;
        using var form = Window();
        using var outline = Outline(form, (_, _) => ++asked > 0);

        outline.DropAt("c", In(outline, 0, 0.4));
        outline.DropAt("c", In(outline, 0, 0.5));
        outline.DropAt("c", In(outline, 0, 0.6));
        Assert.Equal(1, asked);

        outline.DropAt("c", In(outline, 0, 0.9));
        Assert.Equal(2, asked);
    }

    [WinFormsFact]
    public void New_rows_are_asked_about_afresh()
    {
        // A sync can land mid-drag. The rows it brings can put a different task at the same index,
        // so the last answer isn't one to keep.
        var asked = 0;
        using var form = Window();
        using var outline = Outline(form, (_, _) => ++asked > 0);

        outline.DropAt("c", In(outline, 0, 0.5));
        outline.Rows = [Task("a"), Task("b", depth: 1), Task("c")];
        outline.DropAt("c", In(outline, 0, 0.5));

        Assert.Equal(2, asked);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    /// <summary>Scrolls a list by a number of pixels across and rows down.</summary>
    private const int LvmScroll = 0x1014;
}
