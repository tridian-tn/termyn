using System.Diagnostics;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// A description saving itself while it's being typed, rather than only when the box is left.
/// </summary>
public class DescriptionSaveTests
{
    [WinFormsFact]
    public void A_description_saves_once_the_typing_pauses_with_the_keyboard_still_in_the_box()
    {
        // Leaving the box saves it too, but somebody who stops typing and walks away from the
        // machine never leaves it, and what they wrote mustn't be only in the window.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Work","child_order":1}""");
        store.PutResource("items", "a", """{"id":"a","content":"First","description":"Notes","project_id":"p1","child_order":1}""");

        using var window = TestWindow.Build("termyn-description-pause-save.json", store, out _, out var presenter);
        TestWindow.ShowOffScreen(window);
        presenter.Select(ViewSelection.Of(SmartView.All));
        window.ShowPanelTab(comments: false);

        var outline = TestWindow.Find<OutlineView>(window);
        var editor = TestWindow.Find<MarkdownEditor>(window);
        outline.SelectId("a");

        window.Run(AppCommand.EditDescription);
        Application.DoEvents();
        Assert.True(window.NewContext(null).WritingDescription, "the markdown never opened, so this says nothing");
        Assert.True(editor.Focused, "the markdown never took the keyboard, so this says nothing");

        window.WaitBeforeSavingDescription(50);
        editor.AppendText(", and more");

        // Not on the keystroke: that would be a write between every two letters.
        Assert.Equal("Notes", presenter.DescriptionOf(SubjectKind.Task, "a"));

        var waited = Stopwatch.StartNew();
        while (presenter.DescriptionOf(SubjectKind.Task, "a") == "Notes" && waited.Elapsed < TimeSpan.FromSeconds(5))
            Application.DoEvents();

        Assert.Equal("Notes, and more", presenter.DescriptionOf(SubjectKind.Task, "a"));
        Assert.True(editor.Focused, "the keyboard left the markdown, so something other than the pause saved it");
    }
}
