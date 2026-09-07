using System.Text.Json.Nodes;
using Termyn.Core.Model;

namespace Termyn.Core.Tests;

public class ProjectionsTests
{
    [Theory]
    [InlineData("""{"id":"p","name":"In","is_inbox_project":true}""")]
    [InlineData("""{"id":"p","name":"In","inbox_project":true}""")]
    [InlineData("""{"id":"p","name":"In","is_inbox_project":1}""")]
    public void Accepts_either_inbox_field_name_and_integer_flags(string json)
        => Assert.True(Projections.ToProject(Obj(json)).IsInboxProject);

    [Fact]
    public void A_project_without_an_inbox_flag_is_not_the_inbox()
        => Assert.False(Projections.ToProject(Obj("""{"id":"p","name":"Work"}""")).IsInboxProject);

    [Fact]
    public void Reads_due_date_and_text()
    {
        var item = Projections.ToTaskItem(Obj("""{"id":"i","due":{"date":"2026-07-30","string":"Jul 30"}}"""));

        Assert.Equal("2026-07-30", item.DueDate);
        Assert.Equal("Jul 30", item.DueText);
    }

    [Fact]
    public void Reads_the_deadline_off_its_own_object()
    {
        // The one step the filter tests can't reach: they build a task in code, so a "deadline:"
        // term whose field never gets read off the JSON passes every one of them and then matches
        // nothing at all against a real account. The due date and the deadline are separate fields
        // and this asserts on both, since reading one into the other would look right in isolation.
        var item = Projections.ToTaskItem(
            Obj("""{"id":"i","due":{"date":"2026-08-15"},"deadline":{"date":"2026-07-31","lang":"en"}}"""));

        Assert.Equal("2026-07-31", item.Deadline);
        Assert.Equal("2026-08-15", item.DueDate);
    }

    [Theory]
    [InlineData("""{"id":"i"}""")]
    [InlineData("""{"id":"i","deadline":null}""")]
    [InlineData("""{"id":"i","due":{"date":"2026-08-15"}}""")]
    public void A_task_with_no_deadline_has_none(string json)
        => Assert.Null(Projections.ToTaskItem(Obj(json)).Deadline);

    [Theory]

    // What the account sends today, and what it used to send. A "created:" filter reading neither
    // would match nothing at all and look like a filter that ran and found no tasks.
    [InlineData("""{"id":"i","added_at":"2021-09-17T09:59:45.791288Z"}""")]
    [InlineData("""{"id":"i","date_added":"2021-09-17T09:59:45.791288Z"}""")]
    public void Reads_when_a_task_was_added_under_either_field_name(string json)
        => Assert.Equal("2021-09-17T09:59:45.791288Z", Projections.ToTaskItem(Obj(json)).AddedAt);

    [Fact]
    public void A_task_with_no_creation_stamp_has_none_rather_than_a_guess()
        => Assert.Null(Projections.ToTaskItem(Obj("""{"id":"i"}""")).AddedAt);

    [Theory]
    [InlineData("""{"id":"i"}""")]
    [InlineData("""{"id":"i","due":null}""")]
    public void Tolerates_an_absent_due(string json)
    {
        var item = Projections.ToTaskItem(Obj(json));

        Assert.Null(item.DueDate);
        Assert.Null(item.DueText);
    }

    [Theory]
    [InlineData("""{"id":"i","priority":4}""", Priority.P1)]
    [InlineData("""{"id":"i","priority":1}""", Priority.P4)]
    [InlineData("""{"id":"i"}""", Priority.P4)]
    [InlineData("""{"id":"i","priority":0}""", Priority.P4)]
    public void Inverts_priority_and_defaults_a_missing_one(string json, Priority expected)
        => Assert.Equal(expected, Projections.ToTaskItem(Obj(json)).Priority);

    [Theory]
    [InlineData("""{"id":"i","checked":true}""", true)]
    [InlineData("""{"id":"i","checked":1}""", true)]
    [InlineData("""{"id":"i","checked":false}""", false)]
    [InlineData("""{"id":"i","checked":0}""", false)]
    [InlineData("""{"id":"i"}""", false)]
    public void Reads_completion_as_bool_or_integer(string json, bool expected)
        => Assert.Equal(expected, Projections.ToTaskItem(Obj(json)).Completed);

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;
}
