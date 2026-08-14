using System.Text;
using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>
/// Putting a file on a comment, and taking one off.
/// </summary>
/// <remarks>
/// The one write in Termyn that isn't purely a queued command. The upload has to finish before the
/// <c>note_add</c> that references it can be queued, because the command needs the url the upload
/// returns — so there is nothing to write down until the network has answered, and a failure has to
/// leave nothing behind.
/// </remarks>
public class AttachmentWriteTests
{
    private static readonly DateOnly Today = new(2026, 8, 14);

    private static Stream File(string content = "a file") => new MemoryStream(Encoding.UTF8.GetBytes(content));

    // ---- Adding ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_comment_with_a_file_carries_what_the_upload_gave_back()
    {
        var (engine, api) = Seeded();

        await engine.AddCommentWithFileAsync("i1", "here it is", File(), "agenda.pdf");

        var comment = Assert.Single(engine.CommentsFor("i1"));
        Assert.Equal("here it is", comment.Content);
        Assert.Equal("agenda.pdf", comment.Attachment!.FileName);
        Assert.Equal("https://files.todoist.test/agenda.pdf", comment.Attachment.FileUrl);
        Assert.False(comment.Attachment.Pending);

        var args = Json.Object(Assert.Single(engine.Outbox).ArgsJson);
        Assert.Equal("note_add", Assert.Single(engine.Outbox).Type);
        Assert.NotNull(args["file_attachment"]);
    }

    [Fact]
    public async Task The_file_is_uploaded_before_the_command_is_queued()
    {
        // Not an ordering nicety: the command needs the url the upload returns, so there is nothing
        // to queue until it has answered.
        var (engine, api) = Seeded();

        await engine.AddCommentWithFileAsync("i1", string.Empty, File("the bytes"), "a.txt");

        Assert.Equal("the bytes", Encoding.UTF8.GetString(Assert.Single(api.Uploaded)));
    }

    [Fact]
    public async Task A_file_on_its_own_with_nothing_said_is_allowed()
    {
        var (engine, _) = Seeded();

        await engine.AddCommentWithFileAsync("i1", string.Empty, File(), "agenda.pdf");

        var comment = Assert.Single(engine.CommentsFor("i1"));
        Assert.Equal(string.Empty, comment.Content);
        Assert.Equal("agenda.pdf", comment.Attachment!.FileName);
    }

    [Fact]
    public async Task A_failed_upload_queues_nothing_at_all()
    {
        // A comment queued without its file would sync as one that quietly lost its attachment,
        // which is worse than one that never happened.
        var (engine, api) = Seeded();
        api.TransferThrow = new TodoistNetworkException("offline");

        await Assert.ThrowsAsync<TodoistNetworkException>(
            () => engine.AddCommentWithFileAsync("i1", "here it is", File(), "agenda.pdf"));

        Assert.Empty(engine.Outbox);
        Assert.Empty(engine.CommentsFor("i1"));
    }

    [Fact]
    public async Task Attaching_while_signed_out_is_refused_before_anything_is_sent()
    {
        var (engine, api) = Seeded(token: null);

        await Assert.ThrowsAsync<TodoistAuthException>(
            () => engine.AddCommentWithFileAsync("i1", "here it is", File(), "agenda.pdf"));

        Assert.Empty(api.Uploaded);
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public async Task A_task_deleted_while_the_upload_was_in_flight_queues_nothing()
    {
        // The upload happens outside the lock and can take a minute on a large file. The task it
        // was going on may not be there by the time it comes back.
        var (engine, api) = Seeded();
        api.Upload = name =>
        {
            engine.DeleteItem("i1");
            return new JsonObject { ["file_name"] = name, ["file_url"] = "https://files.todoist.test/x" };
        };

        var added = await engine.AddCommentWithFileAsync("i1", "here it is", File(), "agenda.pdf");

        Assert.Null(added);
        Assert.DoesNotContain(engine.Outbox, c => c.Type == "note_add");
    }

    [Fact]
    public async Task Commenting_with_a_file_on_something_not_held_uploads_nothing()
    {
        var (engine, api) = Seeded();

        Assert.Null(await engine.AddCommentWithFileAsync("gone", "here it is", File(), "agenda.pdf"));
        Assert.Empty(api.Uploaded);
    }

    // ---- Taking one off ---------------------------------------------------------------------------

    [Fact]
    public void Detaching_leaves_what_was_said_and_names_the_upload_to_delete()
    {
        var (engine, _) = Seeded((
            "notes",
            "n1",
            """{"id":"n1","item_id":"i1","content":"see attached","file_attachment":{"file_name":"a.pdf","file_url":"https://files.todoist.test/a"}}"""));

        var url = engine.DetachFile("n1");

        Assert.Equal("https://files.todoist.test/a", url);

        var comment = Assert.Single(engine.CommentsFor("i1"));
        Assert.Equal("see attached", comment.Content);
        Assert.Null(comment.Attachment);
    }

    [Fact]
    public void Detaching_sends_the_field_as_cleared_rather_than_leaving_it_out()
    {
        // A command that simply omits the field is a command that changes nothing — field-level
        // writes are a patch, so clearing has to be said explicitly.
        var (engine, _) = Seeded((
            "notes",
            "n1",
            """{"id":"n1","item_id":"i1","content":"see attached","file_attachment":{"file_name":"a.pdf","file_url":"https://files.todoist.test/a"}}"""));

        engine.DetachFile("n1");

        var args = Json.Object(Assert.Single(engine.Outbox).ArgsJson);
        Assert.True(args.ContainsKey("file_attachment"));
        Assert.Null(args["file_attachment"]);
    }

    [Fact]
    public void Detaching_from_a_comment_with_no_file_does_nothing()
    {
        var (engine, _) = Seeded(("notes", "n1", """{"id":"n1","item_id":"i1","content":"words only"}"""));

        Assert.Null(engine.DetachFile("n1"));
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public async Task Deleting_the_upload_itself_goes_straight_to_the_server()
    {
        // Todoist has no command for it, so the outbox has nowhere to put it — which makes it the
        // one write here that simply fails when there is no connection rather than waiting for one.
        var (engine, api) = Seeded();

        await engine.DeleteUploadAsync("https://files.todoist.test/a");

        Assert.Equal("https://files.todoist.test/a", Assert.Single(api.DeletedUploads));
        Assert.Empty(engine.Outbox);
    }

    [Fact]
    public async Task Deleting_the_upload_offline_fails_rather_than_queueing()
    {
        var (engine, api) = Seeded();
        api.TransferThrow = new TodoistNetworkException("offline");

        await Assert.ThrowsAsync<TodoistNetworkException>(() => engine.DeleteUploadAsync("https://files.todoist.test/a"));

        Assert.Empty(engine.Outbox);
    }

    // ---- Setup ------------------------------------------------------------------------------------

    private static (SyncEngine Engine, FakeApi Api) Seeded(params (string Type, string Id, string Json)[] resources)
        => Seeded("tok", resources);

    private static (SyncEngine Engine, FakeApi Api) Seeded(string? token, params (string Type, string Id, string Json)[] resources)
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("items", "i1", """{"id":"i1","content":"Write it up","project_id":"p1"}""");
        store.PutResource("projects", "p1", """{"id":"p1","name":"Home"}""");

        foreach (var (type, id, json) in resources)
            store.PutResource(type, id, json);

        var api = new FakeApi();
        var engine = new SyncEngine(api, store, new FakeSecrets { Stored = token }, new FixedClock(Today));
        engine.Load();
        return (engine, api);
    }
}
