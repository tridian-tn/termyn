using Termyn.Core.Model;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

/// <summary>
/// The comments pane used to be aimed: at the task the outline was on, or at a project by way of a
/// menu entry that existed for no other reason. These are the tests for reading it off the selection
/// instead — which is the whole of what the pane now decides.
/// </summary>
public class PanelSubjectTests
{
    private static TaskRow Task(string id, string content)
        => new(id, content, Priority.P4, "Project", string.Empty, []);

    private static SidebarNode Node(SidebarKind kind, string id, string label)
        => new(kind, id, label, 0, $"{kind}:{id}");

    [Fact]
    public void A_task_you_have_picked_out_is_what_the_comments_are_of()
    {
        var subject = PanelSubject.Of(Task("t1", "Book the van"), Node(SidebarKind.Project, "p1", "Moving"));

        Assert.Equal("t1", subject.Id);
        Assert.Equal("Task: Book the van", subject.About);
    }

    [Fact]
    public void The_project_you_are_in_is_what_they_are_of_until_you_do()
    {
        // The point of the change: standing in a project with no task selected is asking about the
        // project, and used to show nothing at all unless you found the menu entry for it.
        var subject = PanelSubject.Of(null, Node(SidebarKind.Project, "p1", "Moving"));

        Assert.Equal("p1", subject.Id);
        Assert.Equal("Project: Moving", subject.About);
    }

    [Theory]
    [InlineData(SidebarKind.SmartView)]
    [InlineData(SidebarKind.Section)]
    [InlineData(SidebarKind.Label)]
    [InlineData(SidebarKind.Filter)]
    public void Only_a_project_stands_in_for_a_task(SidebarKind kind)
    {
        // Todoist hangs comments off tasks and projects and nothing else, so a label or a filter
        // with no task selected leaves the pane with nothing to show rather than something wrong.
        var subject = PanelSubject.Of(null, Node(kind, "x1", "Somewhere"));

        Assert.Null(subject.Id);
        Assert.Equal(string.Empty, subject.About);
    }

    [Fact]
    public void Nothing_selected_anywhere_is_nothing_to_show()
    {
        Assert.Equal(PanelSubject.None, PanelSubject.Of(null, null));
    }

    [Fact]
    public void A_task_stands_on_its_own_without_a_sidebar_row_behind_it()
    {
        // Search results and the smart views leave the sidebar on something that owns no comments,
        // or on nothing — neither of which should cost the selected task its own.
        var subject = PanelSubject.Of(Task("t2", "Ring the bank"), null);

        Assert.Equal("t2", subject.Id);
        Assert.Equal("Task: Ring the bank", subject.About);
    }
}
