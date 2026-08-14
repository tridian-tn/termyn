using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// Comments on tasks and on projects.
/// </summary>
/// <remarks>
/// Todoist files the two apart — task comments under <c>notes</c>, project comments under
/// <c>project_notes</c> — while writing both with the same <c>note_*</c> command. Most of what is
/// worth testing here comes out of that: the command alone can't say which of the two a write is
/// aimed at, and getting it wrong loses a server change for good.
/// </remarks>
public class CommentTests
{
    private static readonly DateOnly Today = new(2026, 8, 14);

    [Fact]
    public void The_comments_on_a_task_come_back_oldest_first()
    {
        var engine = Seeded(
            ("notes", "n2", """{"id":"n2","item_id":"i1","content":"second","posted_at":"2026-08-12T10:00:00Z"}"""),
            ("notes", "n1", """{"id":"n1","item_id":"i1","content":"first","posted_at":"2026-08-11T10:00:00Z"}"""));

        Assert.Equal(["first", "second"], engine.CommentsFor("i1").Select(c => c.Content));
    }

    [Fact]
    public void A_comment_belongs_to_the_thing_it_hangs_off_and_not_to_anything_else()
    {
        var engine = Seeded(
            ("notes", "n1", """{"id":"n1","item_id":"i1","content":"on the task"}"""),
            ("project_notes", "pn1", """{"id":"pn1","project_id":"p1","content":"on the project"}"""));

        Assert.Equal("on the task", Assert.Single(engine.CommentsFor("i1")).Content);
        Assert.Equal("on the project", Assert.Single(engine.CommentsFor("p1")).Content);
        Assert.Empty(engine.CommentsFor("nothing"));
        Assert.Empty(engine.CommentsFor(null));
    }

    // ---- Writing ------------------------------------------------------------------------------

    [Fact]
    public void A_comment_added_offline_shows_at_once_and_queues_one_command()
    {
        var engine = Seeded();

        var id = engine.AddComment("i1", "said while offline");

        Assert.NotNull(id);
        Assert.Equal("said while offline", Assert.Single(engine.CommentsFor("i1")).Content);

        var queued = Assert.Single(engine.Outbox);
        Assert.Equal("note_add", queued.Type);

        var args = Json.Object(queued.ArgsJson);
        Assert.Equal("said while offline", args["content"]!.ToString());
        Assert.Equal("i1", args["item_id"]!.ToString());
        Assert.Null(args["project_id"]);
    }

    [Fact]
    public void A_comment_on_a_project_names_the_project_rather_than_a_task()
    {
        var engine = Seeded();

        engine.AddComment("p1", "about the project");

        var args = Json.Object(Assert.Single(engine.Outbox).ArgsJson);
        Assert.Equal("p1", args["project_id"]!.ToString());
        Assert.Null(args["item_id"]);
    }

    [Fact]
    public void Commenting_on_something_the_account_does_not_hold_queues_nothing()
    {
        var engine = Seeded();

        Assert.Null(engine.AddComment("gone", "into the void"));
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public async Task A_comment_added_offline_reaches_the_account_on_reconnect()
    {
        var (engine, api) = SeededWithApi();
        var temp = engine.AddComment("i1", "written on a train")!;

        api.Next = commands =>
        {
            var add = Assert.Single(commands);
            return new SyncResponse
            {
                SyncToken = "s2",
                SyncStatus = new Dictionary<string, CommandResult> { [add.Uuid] = new(true, null, null) },
                TempIdMapping = new Dictionary<string, string> { [add.TempId!] = "n-real" },
            };
        };

        await engine.SyncAsync();

        var comment = Assert.Single(engine.CommentsFor("i1"));
        Assert.Equal("n-real", comment.Id);
        Assert.Equal("written on a train", comment.Content);
        Assert.Empty(engine.Outbox);
        Assert.NotEqual(temp, comment.Id);
    }

    [Fact]
    public async Task A_comment_written_on_the_web_arrives_on_sync()
    {
        var (engine, api) = SeededWithApi();
        api.Response = new SyncResponse
        {
            SyncToken = "s2",
            Changes = [Json.Change("notes", "n9", """{"id":"n9","item_id":"i1","content":"from the web"}""")],
        };

        await engine.SyncAsync();

        Assert.Equal("from the web", Assert.Single(engine.CommentsFor("i1")).Content);
    }

    [Fact]
    public void Editing_a_comment_queues_the_content_and_nothing_else()
    {
        var engine = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"before"}"""));

        engine.EditComment("n1", "after");

        Assert.Equal("after", Assert.Single(engine.CommentsFor("i1")).Content);

        var queued = Assert.Single(engine.Outbox);
        Assert.Equal("note_update", queued.Type);

        var args = Json.Object(queued.ArgsJson);
        Assert.Equal(["content", "id"], args.Select(a => a.Key).Order().ToArray());
    }

    [Fact]
    public void Deleting_a_comment_takes_it_away_and_queues_the_delete()
    {
        var engine = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"regretted"}"""));

        engine.DeleteComment("n1");

        Assert.Empty(engine.CommentsFor("i1"));
        Assert.Equal("note_delete", Assert.Single(engine.Outbox).Type);
    }

    [Fact]
    public void Editing_or_deleting_something_that_is_not_a_comment_does_nothing()
    {
        var engine = Seeded();

        engine.EditComment("i1", "a task is not a comment");
        engine.DeleteComment("p1");
        engine.EditComment("nothing at all", "neither is this");

        Assert.Empty(engine.Outbox);
    }

    // ---- Which of the two a write is aimed at ---------------------------------------------------

    [Fact]
    public async Task An_un_acked_edit_to_a_project_comment_is_not_overwritten_by_the_server()
    {
        // The one that made the command-to-type mapping worth having. A note_update carries only an
        // id, so nothing in the command says whether it is filed under notes or project_notes. Get
        // it wrong and the pending edit fails to shield the resource, the server's copy lands on top
        // of it, and the sync token has already moved past that change — so the edit is gone.
        var (engine, api) = SeededWithApi(
            ("project_notes", "pn1", """{"id":"pn1","project_id":"p1","content":"before"}"""));

        engine.EditComment("pn1", "my edit, not yet sent");

        api.Response = new SyncResponse
        {
            SyncToken = "s2",
            Changes = [Json.Change("project_notes", "pn1", """{"id":"pn1","project_id":"p1","content":"the server's copy"}""")],
        };

        await engine.SyncAsync();

        Assert.Equal("my edit, not yet sent", Assert.Single(engine.CommentsFor("p1")).Content);
    }

    [Fact]
    public async Task A_project_comment_created_offline_is_promoted_where_it_actually_lives()
    {
        var (engine, api) = SeededWithApi();
        engine.AddComment("p1", "queued against a project");

        api.Next = commands => new SyncResponse
        {
            SyncToken = "s2",
            SyncStatus = new Dictionary<string, CommandResult> { [commands[0].Uuid] = new(true, null, null) },
            TempIdMapping = new Dictionary<string, string> { [commands[0].TempId!] = "pn-real" },
        };

        await engine.SyncAsync();

        Assert.Equal("pn-real", Assert.Single(engine.CommentsFor("p1")).Id);
    }

    // ---- Fidelity -------------------------------------------------------------------------------

    [Fact]
    public void A_field_this_client_does_not_model_survives_an_edit()
    {
        var engine = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"before","reactions":{"tada":["u1"]}}"""));

        engine.EditComment("n1", "after");

        var raw = engine.RawResource("notes", "n1")!;
        Assert.Equal("after", raw["content"]!.ToString());
        Assert.Equal("""{"tada":["u1"]}""", raw["reactions"]!.ToJsonString());
    }

    [Fact]
    public void An_attachment_is_named_even_though_fetching_it_is_a_later_phase()
    {
        // A comment can carry a file and no words at all. Drawn from its content alone that is a
        // blank row, which reads as a bug rather than as a file.
        var engine = Seeded((
            "notes",
            "n1",
            """{"id":"n1","item_id":"i1","content":"","file_attachment":{"file_name":"agenda.pdf","file_type":"application/pdf"}}"""));

        var comment = Assert.Single(engine.CommentsFor("i1"));
        Assert.Equal("agenda.pdf", comment.AttachmentName);
        Assert.Equal(string.Empty, comment.Content);
    }

    [Fact]
    public void A_comment_with_no_file_says_so_rather_than_naming_one()
        => Assert.Null(Assert.Single(
            Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"words only"}""")).CommentsFor("i1")).AttachmentName);

    // ---- Ordering when the server has not spoken yet --------------------------------------------

    [Fact]
    public void One_just_written_sorts_after_the_ones_the_server_has_timed()
    {
        var engine = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"posted","posted_at":"2026-08-11T10:00:00Z"}"""));

        engine.AddComment("i1", "just now");

        Assert.Equal(["posted", "just now"], engine.CommentsFor("i1").Select(c => c.Content));
    }

    // ---- Setup ----------------------------------------------------------------------------------

    private static SyncEngine Seeded(params (string Type, string Id, string Json)[] resources)
        => SeededWithApi(resources).Engine;

    private static (SyncEngine Engine, FakeApi Api) SeededWithApi(params (string Type, string Id, string Json)[] resources)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Write it up","project_id":"p1"}""");
        store.PutResource("projects", "p1", """{"id":"p1","name":"Home"}""");

        foreach (var (type, id, json) in resources)
            store.PutResource(type, id, json);

        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = "tok" }, new FixedClock(Today));
        engine.Load();
        return (engine, api);
    }
}
