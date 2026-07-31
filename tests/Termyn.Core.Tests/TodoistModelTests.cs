using System.Text.Json.Nodes;
using Termyn.Core.Model;

namespace Termyn.Core.Tests;

/// <summary>
/// Reading tasks back out of the model after it has been changed.
/// </summary>
/// <remarks>
/// The model caches projected tasks so a publish doesn't re-parse the whole account. Three of these
/// are what stop that cache going stale, and fail if its invalidation is removed: the two about an
/// edit being visible, and the one about repointing a reference. The rest hold whatever path they
/// describe and would pass with no cache at all — which is the point, since that is what the model
/// is meant to look like from the outside.
/// </remarks>
public class TodoistModelTests
{
    [Fact]
    public void Reading_twice_gives_the_same_answer()
    {
        var model = WithTask("i1", "A");

        Assert.Equal("A", model.Items().Single().Content);
        Assert.Equal("A", model.Items().Single().Content);
    }

    [Fact]
    public void An_edit_is_visible_immediately()
    {
        var model = WithTask("i1", "A");
        _ = model.Items().ToList(); // cache it

        model.Upsert(ResourceType.Items, "i1", Parse("""{"id":"i1","content":"B"}"""));

        Assert.Equal("B", model.Items().Single().Content);
    }

    [Fact]
    public void Every_projected_field_follows_the_edit_not_just_the_content()
    {
        var model = WithTask("i1", "A");
        _ = model.Items().ToList();

        model.Upsert(ResourceType.Items, "i1", Parse(
            """{"id":"i1","content":"A","checked":true,"priority":4,"labels":["urgent"],"due":{"date":"2026-08-01","is_recurring":true}}"""));

        var item = model.Items().Single();
        Assert.True(item.Completed);
        Assert.Equal(Priority.P1, item.Priority);
        Assert.Equal(["urgent"], item.Labels);
        Assert.Equal("2026-08-01", item.DueDate);
        Assert.True(item.IsRecurring);
    }

    [Fact]
    public void A_removed_task_does_not_come_back()
    {
        var model = WithTask("i1", "A");
        _ = model.Items().ToList();

        model.Remove(ResourceType.Items, "i1");

        Assert.Empty(model.Items());
    }

    [Fact]
    public void A_task_removed_and_recreated_under_the_same_id_shows_the_new_one()
    {
        // Which is what a delete followed by the server reusing the id would look like.
        var model = WithTask("i1", "A");
        _ = model.Items().ToList();

        model.Remove(ResourceType.Items, "i1");
        model.Upsert(ResourceType.Items, "i1", Parse("""{"id":"i1","content":"Different"}"""));

        Assert.Equal("Different", model.Items().Single().Content);
    }

    [Fact]
    public void A_temporary_id_becoming_real_carries_the_task_with_it()
    {
        var model = WithTask("t-1", "A");
        _ = model.Items().ToList();

        model.Rename(ResourceType.Items, "t-1", "real-1");

        var item = model.Items().Single();
        Assert.Equal("real-1", item.Id);
        Assert.Equal("A", item.Content);
    }

    [Fact]
    public void Repointing_a_reference_is_visible_even_though_it_edits_the_json_in_place()
    {
        // RewriteReferences mutates the retained object rather than replacing it, so it is the one
        // path the cache doesn't hear about from Upsert.
        var model = new TodoistModel();
        model.Upsert(ResourceType.Items, "child", Parse("""{"id":"child","content":"C","parent_id":"t-1"}"""));
        Assert.Equal("t-1", model.Items().Single().ParentId);

        _ = model.RewriteReferences("t-1", "real-1").ToList();

        Assert.Equal("real-1", model.Items().Single().ParentId);
    }

    [Fact]
    public void Clearing_the_model_clears_what_was_projected_from_it()
    {
        var model = WithTask("i1", "A");
        _ = model.Items().ToList();

        model.Clear();

        Assert.Empty(model.Items());
    }

    [Fact]
    public void A_task_reloaded_after_a_clear_is_read_afresh()
    {
        var model = WithTask("i1", "A");
        _ = model.Items().ToList();

        model.Clear();
        model.Upsert(ResourceType.Items, "i1", Parse("""{"id":"i1","content":"From the cache file"}"""));

        Assert.Equal("From the cache file", model.Items().Single().Content);
    }

    [Fact]
    public void Other_resource_types_are_unaffected_by_a_task_changing()
    {
        var model = WithTask("i1", "A");
        model.Upsert(ResourceType.Projects, "p1", Parse("""{"id":"p1","name":"Work"}"""));
        _ = model.Items().ToList();

        model.Upsert(ResourceType.Items, "i1", Parse("""{"id":"i1","content":"B"}"""));

        Assert.Equal("Work", model.Projects().Single().Name);
    }

    private static TodoistModel WithTask(string id, string content)
    {
        var model = new TodoistModel();
        model.Upsert(ResourceType.Items, id, Parse($$"""{"id":"{{id}}","content":"{{content}}"}"""));
        return model;
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
}
