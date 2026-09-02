using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// Reading and writing the description on a project, which Todoist has and Termyn used to ignore.
/// </summary>
/// <remarks>
/// The panel follows the selection, so standing in a project with no task picked out puts the
/// project in the panel — and a Description tab that answered "select a task" from there was the
/// panel refusing to show something the account plainly holds.
/// </remarks>
public class ProjectDescriptionTests
{
    private static readonly DateOnly Today = new(2026, 7, 31);

    [Fact]
    public void A_projects_description_is_the_markdown_the_account_holds()
    {
        var presenter = Seeded("**How this project works**");

        Assert.Equal("**How this project works**", presenter.DescriptionOf(SubjectKind.Project, "p1"));
    }

    [Fact]
    public void A_project_with_no_description_has_an_empty_one_rather_than_none()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Bare"}""");

        Assert.Equal(string.Empty, Presenter(store).DescriptionOf(SubjectKind.Project, "p1"));
    }

    [Fact]
    public void Writing_a_projects_description_queues_one_field_and_shows_it_at_once()
    {
        var (presenter, engine) = SeededWith("Before");

        presenter.SetDescription(SubjectKind.Project, "p1", "After");

        Assert.Equal("After", presenter.DescriptionOf(SubjectKind.Project, "p1"));

        var queued = Assert.Single(engine.Outbox);
        Assert.Equal("project_update", queued.Type);

        var args = JsonNode.Parse(queued.ArgsJson)!.AsObject();
        Assert.Equal("After", args["description"]!.ToString());
        Assert.Equal("p1", args["id"]!.ToString());
        Assert.Equal(["description", "id"], args.Select(a => a.Key).Order().ToArray());
    }

    [Fact]
    public void Writing_a_projects_description_leaves_the_rest_of_it_alone()
    {
        // A patch, not a replacement. The project's name is what the sidebar draws, and a write
        // that carried a stale copy of it would rename the project from under the user.
        var (presenter, _) = SeededWith("Before");

        presenter.SetDescription(SubjectKind.Project, "p1", "After");

        var project = Assert.Single(presenter.Sidebar, n => n is { Kind: SidebarKind.Project, Id: "p1" });
        Assert.Equal("Groundworks", project.Label);
    }

    [Fact]
    public void The_two_kinds_are_kept_apart_even_when_they_share_an_id()
    {
        // Todoist ids are only unique within a resource type, so a task and a project really can
        // both be "1". Whichever store were tried first, a lookup that fell through from one to the
        // other would answer with the wrong thing's description — and read as the right one.
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "1", """{"id":"1","content":"A task","project_id":"p","description":"the task's"}""");
        store.PutResource("projects", "1", """{"id":"1","name":"A project","description":"the project's"}""");

        var presenter = Presenter(store);

        Assert.Equal("the task's", presenter.DescriptionOf(SubjectKind.Task, "1"));
        Assert.Equal("the project's", presenter.DescriptionOf(SubjectKind.Project, "1"));
    }

    [Fact]
    public void Nothing_selected_can_be_neither_read_nor_written()
    {
        var (presenter, engine) = SeededWith("Before");

        Assert.Equal(string.Empty, presenter.DescriptionOf(SubjectKind.None, "p1"));
        Assert.Equal(string.Empty, presenter.DescriptionOf(SubjectKind.Project, null));
        Assert.Equal(DescriptionAccess.Nothing, presenter.AccessToDescriptionOf(SubjectKind.None, "p1"));

        presenter.SetDescription(SubjectKind.None, "p1", "After");
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public void A_project_the_account_holds_can_be_written_to_and_one_it_doesnt_cannot()
    {
        // What decides whether the box is read-only. An edit queued against something the model
        // has never heard of goes nowhere, so the panel has to know before it offers to take one.
        var (presenter, _) = SeededWith("Before");

        Assert.Equal(DescriptionAccess.Writable, presenter.AccessToDescriptionOf(SubjectKind.Project, "p1"));
        Assert.Equal(DescriptionAccess.ReadOnly, presenter.AccessToDescriptionOf(SubjectKind.Project, "gone"));
    }

    [Fact]
    public void The_inbox_takes_no_description_at_all()
    {
        // Todoist accepts the command, reports success, and stores nothing — so a box that took the
        // typing would look saved and be gone by the next full sync, with nothing said. Checked
        // against the live account before this was written, on the Inbox and on an ordinary project.
        var store = new InMemorySnapshotStore();
        store.PutResource("projects", "p1", """{"id":"p1","name":"Inbox","is_inbox_project":true}""");
        store.PutResource("projects", "p2", """{"id":"p2","name":"Groundworks"}""");

        var presenter = Presenter(store);

        Assert.Equal(DescriptionAccess.NotKept, presenter.AccessToDescriptionOf(SubjectKind.Project, "p1"));
        Assert.Equal(DescriptionAccess.Writable, presenter.AccessToDescriptionOf(SubjectKind.Project, "p2"));
    }

    [Fact]
    public void Only_a_real_project_reaches_a_description_at_all()
    {
        // Today, Upcoming, the Inbox smart view, a label, a filter, a section: none of them is a
        // thing Todoist keeps a description on. They are kept out one step earlier — the subject
        // only ever names a task or a project — so nothing here has to know about them one by one.
        var kinds = new[]
        {
            SidebarKind.SmartView, SidebarKind.Section, SidebarKind.Label, SidebarKind.Filter, SidebarKind.Header,
        };

        foreach (var kind in kinds)
        {
            var node = new SidebarNode(kind, "x1", "Today", 0, $"{kind}:x1");
            Assert.Equal(SubjectKind.None, PanelSubject.Of(null, node).Kind);
        }
    }

    private static MainPresenter Seeded(string description) => SeededWith(description).Presenter;

    private static (MainPresenter Presenter, SyncEngine Engine) SeededWith(string description)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource(
            "projects",
            "p1",
            new JsonObject
            {
                ["id"] = "p1",
                ["name"] = "Groundworks",
                ["description"] = description,
            }.ToJsonString());

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return (presenter, engine);
    }

    private static MainPresenter Presenter(InMemorySnapshotStore store)
    {
        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();

        var presenter = new MainPresenter(engine, new QuickAddParser(new FixedClock(Today)));
        presenter.Select(ViewSelection.Of(SmartView.All));
        return presenter;
    }
}
