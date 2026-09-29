using Termyn.Core.Settings;
using Termyn.Core.Sync;
using Termyn.Presentation;
using SmartView = Termyn.Core.Model.SmartView;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// A window over an account holding two labels of one name.
/// </summary>
/// <remarks>
/// Nothing stops it: renaming one label onto another is enough. The window used to key the labels'
/// colours by name, which threw on the second one, and since that was part of drawing the window,
/// every sync after it put up an unhandled-exception dialog and left the outline where it was.
/// </remarks>
public class DuplicateLabelTests
{
    [WinFormsFact]
    public void A_window_over_two_labels_of_one_name_still_shows_the_tasks()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","color":"teal","item_order":1}""");
        store.PutResource("labels", "l2", """{"id":"l2","name":"home","color":"grape","item_order":2}""");
        store.PutResource("items", "a", """{"id":"a","content":"Ship it","project_id":"p1","labels":["home"],"child_order":1}""");

        using var window = TestWindow.Build("termyn-duplicate-labels.json", store, out _, out var presenter);

        // Shown, off-screen: a window that was never displayed has no handle, and drops the
        // render this is about rather than running it.
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-2000, -2000);
        window.Show();

        presenter.Select(ViewSelection.Of(SmartView.All));

        var outline = TestWindow.Find<OutlineView>(window);
        Assert.Equal(["Ship it"], outline.Rows.Select(r => r.Content));

        // And the list was handed the label's colour: the first of the two, as the sidebar shows it.
        Assert.Equal(Theme.ToColor(TodoistPalette.Of("teal")), outline.LabelColours["home"]);
    }
}
