using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Sync;
using Termyn.TestSupport;

namespace Termyn.Core.Tests;

/// <summary>Label operations. Tasks hold labels by name, which is what makes these awkward.</summary>
public class SyncEngineLabelTests
{
    [Fact]
    public void Setting_the_labels_on_a_task_queues_an_item_update()
    {
        var engine = Seeded();

        engine.SetItemLabels("i1", ["home", "errand"]);

        Assert.Equal(["home", "errand"], engine.Snapshot().Items.Single(i => i.Id == "i1").Labels);

        var cmd = engine.Outbox.Single();
        Assert.Equal("item_update", cmd.Type);
        Assert.Equal(["home", "errand"], Args(cmd)["labels"]!.AsArray().Select(n => n!.ToString()));
    }

    [Fact]
    public void The_same_label_twice_is_only_sent_once()
    {
        var engine = Seeded();

        engine.SetItemLabels("i1", ["home", "HOME"]);

        Assert.Equal(["home"], engine.Snapshot().Items.Single(i => i.Id == "i1").Labels);
    }

    [Fact]
    public void Clearing_the_labels_sends_an_empty_list_not_a_missing_field()
    {
        // Omitting the field would leave the labels in place: a field-level update only touches
        // what it names.
        var engine = Seeded();

        engine.SetItemLabels("i2", []);

        Assert.Empty(engine.Snapshot().Items.Single(i => i.Id == "i2").Labels);
        Assert.Empty(Args(engine.Outbox.Single())["labels"]!.AsArray());
    }

    [Fact]
    public void Adding_a_label_shows_it_immediately_and_queues_label_add()
    {
        var engine = Seeded();

        var id = engine.AddLabel("urgent");

        Assert.Equal("urgent", engine.Snapshot().Labels.Single(l => l.Id == id).Name);
        Assert.Equal("label_add", engine.Outbox.Single().Type);
    }

    [Fact]
    public void Renaming_a_label_queues_label_update()
    {
        var engine = Seeded();

        engine.RenameLabel("l1", "household");

        Assert.Equal("household", engine.Snapshot().Labels.Single(l => l.Id == "l1").Name);

        var cmd = engine.Outbox.Single();
        Assert.Equal("label_update", cmd.Type);
        Assert.Equal("household", Args(cmd)["name"]!.ToString());
    }

    [Fact]
    public void Renaming_a_label_leaves_the_tasks_wearing_it_alone()
    {
        // Only the server knows whether the rename carried across to tasks. Rewriting them here
        // would put a label on screen that the account might not have.
        var engine = Seeded();

        engine.RenameLabel("l1", "household");

        Assert.Equal(["home", "work"], engine.Snapshot().Items.Single(i => i.Id == "i2").Labels);
        Assert.Single(engine.Outbox); // no item_update went with it
    }

    [Fact]
    public void Favouriting_a_label_queues_label_update()
    {
        var engine = Seeded();

        engine.SetLabelFavorite("l1", true);

        Assert.True(engine.Snapshot().Labels.Single(l => l.Id == "l1").IsFavorite);
        Assert.True(Args(engine.Outbox.Single())["is_favorite"]!.GetValue<bool>());
    }

    [Fact]
    public void Deleting_a_label_takes_it_off_the_tasks_too()
    {
        // Todoist cascades the delete onto tasks, so a task still showing the label would be a
        // local fiction.
        var engine = Seeded();

        engine.DeleteLabel("l1");

        Assert.Empty(engine.Snapshot().Labels);
        Assert.Equal(["work"], engine.Snapshot().Items.Single(i => i.Id == "i2").Labels);

        var cmd = engine.Outbox.Single();
        Assert.Equal("label_delete", cmd.Type);
        Assert.Equal("all", Args(cmd)["cascade"]!.ToString());
    }

    [Fact]
    public void Deleting_a_label_leaves_tasks_that_never_had_it()
    {
        var engine = Seeded();

        engine.DeleteLabel("l1");

        Assert.Empty(engine.Snapshot().Items.Single(i => i.Id == "i1").Labels);
    }

    [Fact]
    public void A_queued_label_delete_can_be_reverted_labels_and_all()
    {
        var engine = Seeded();
        engine.DeleteLabel("l1");

        engine.Revert(engine.Outbox.Single().Uuid);

        Assert.Equal("home", engine.Snapshot().Labels.Single().Name);
        Assert.Equal(["home", "work"], engine.Snapshot().Items.Single(i => i.Id == "i2").Labels);
        Assert.Equal(0, engine.PendingCount);
    }

    [Fact]
    public void Undo_reverses_a_label_delete_that_has_not_been_sent()
    {
        var engine = Seeded();
        engine.DeleteLabel("l1");

        Assert.True(engine.Undo());
        Assert.Single(engine.Snapshot().Labels);
    }

    [Fact]
    public void Deleting_an_unknown_label_does_nothing()
    {
        var engine = Seeded();

        engine.DeleteLabel("ghost");

        Assert.Equal(0, engine.PendingCount);
        Assert.Single(engine.Snapshot().Labels);
    }

    private static SyncEngine Seeded()
    {
        var store = new InMemorySnapshotStore();
        store.PutResource("labels", "l1", """{"id":"l1","name":"home","item_order":1}""");
        store.PutResource("items", "i1", """{"id":"i1","content":"No labels","project_id":"p","child_order":1}""");
        store.PutResource("items", "i2", """{"id":"i2","content":"Labelled","project_id":"p","child_order":2,"labels":["home","work"]}""");

        var engine = new SyncEngine(new FakeApi(), store, new FakeSecrets { Stored = "tok" }, new FixedClock(new DateOnly(2026, 7, 31)));
        engine.Load();
        return engine;
    }

    private static JsonObject Args(OutboxCommand cmd) => JsonNode.Parse(cmd.ArgsJson)!.AsObject();
}
