using Termyn.Core.Model;
using Termyn.Core.Settings;
using Termyn.Presentation;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// What the outline says about its selection when the rows underneath it are replaced.
/// </summary>
/// <remarks>
/// A background sync republishes every forty-five seconds, so this happens constantly and under
/// nobody's control. Anything listening takes it as the user having moved — so it has to be said
/// once, and only when the selection has really gone somewhere.
/// </remarks>
public class OutlineSelectionTests
{
    private static TaskRow Row(string id) => new(id, id + " task", Priority.P4, "Work", string.Empty, []);

    private static OutlineView Outline()
    {
        var view = new OutlineView { Theme = Theme.Resolve(ThemePreference.Light) };
        view.CreateControl();
        return view;
    }

    /// <summary>Counts what the outline publishes while the rows are swapped.</summary>
    private static List<string?> Watch(OutlineView view, Action change)
    {
        var seen = new List<string?>();
        void Record(object? sender, EventArgs e) => seen.Add(view.SelectedId);

        view.SelectedIndexChanged += Record;
        try
        {
            change();
        }
        finally
        {
            view.SelectedIndexChanged -= Record;
        }

        return seen;
    }

    [Fact]
    public void A_task_that_moves_up_the_list_is_never_reported_as_no_task_at_all()
    {
        // The selection is re-seated by clearing it and adding the new index, and the moment in
        // between has nothing selected. Published, that reads as the user stepping off the task —
        // and what listens to it goes and does something about that, mid-sentence.
        using var view = Outline();
        view.Rows = [Row("a"), Row("b"), Row("c")];
        view.SelectedIndices.Add(1);

        var seen = Watch(view, () => view.Rows = [Row("new"), Row("a"), Row("b"), Row("c")]);

        Assert.DoesNotContain(null, seen);
        Assert.Equal("b", view.SelectedId);
    }

    [Fact]
    public void A_task_that_moves_is_reported_once()
    {
        using var view = Outline();
        view.Rows = [Row("a"), Row("b"), Row("c")];
        view.SelectedIndices.Add(1);

        var seen = Watch(view, () => view.Rows = [Row("new"), Row("a"), Row("b"), Row("c")]);

        Assert.Equal(["b"], seen);
    }

    [Fact]
    public void A_refresh_that_moves_nothing_says_nothing()
    {
        // The common case by far: a sync that changed something elsewhere in the account.
        using var view = Outline();
        view.Rows = [Row("a"), Row("b"), Row("c")];
        view.SelectedIndices.Add(1);

        var seen = Watch(view, () => view.Rows = [Row("a"), Row("b"), Row("c")]);

        Assert.Empty(seen);
    }

    [Fact]
    public void The_selected_task_going_away_is_reported_once_as_nothing_selected()
    {
        using var view = Outline();
        view.Rows = [Row("a"), Row("b"), Row("c")];
        view.SelectedIndices.Add(1);

        var seen = Watch(view, () => view.Rows = [Row("a"), Row("c")]);

        Assert.Equal([null], seen);
        Assert.Null(view.SelectedId);
    }

    [Fact]
    public void Choosing_a_row_is_still_reported()
    {
        // The suppression is only for the re-seat. A real selection has to come through, or nothing
        // downstream of it ever happens.
        using var view = Outline();
        view.Rows = [Row("a"), Row("b"), Row("c")];

        var seen = Watch(view, () => view.SelectedIndices.Add(2));

        Assert.Equal(["c"], seen);
    }
}
