using System.Globalization;
using System.Text.Json.Nodes;
using Termyn.Core.Model;

namespace Termyn.Core.Capture;

/// <summary>Builds the field sets Termyn sends when creating or editing a task.</summary>
public static class ItemFields
{
    /// <summary>
    /// Turns a locally parsed capture into <c>item_add</c> fields. The caller resolves the project
    /// and section ids first, so it can tell an unmatched name from an absent one and flag it;
    /// leaving them null puts the task in the Inbox.
    /// </summary>
    public static JsonObject ForAdd(QuickAddParse parse, string? projectId = null, string? sectionId = null)
    {
        var fields = new JsonObject { ["content"] = parse.Content };

        if (projectId is not null)
            fields["project_id"] = projectId;

        if (sectionId is not null)
            fields["section_id"] = sectionId;

        if (parse.Labels.Count > 0)
        {
            var labels = new JsonArray();
            foreach (var label in parse.Labels)
                labels.Add(label);
            fields["labels"] = labels;
        }

        if (parse.Priority != Priority.P4)
            fields["priority"] = PriorityMap.ToApi(parse.Priority);

        if (Due(parse.DueDate, parse.DueTime) is { } due)
            fields["due"] = due;

        return fields;
    }

    /// <summary>
    /// Builds a <c>due</c> object. A date on its own is a floating all-day date; adding a time makes
    /// it a specific moment. Returns <c>null</c> when there is no date, which clears any due date.
    /// </summary>
    public static JsonObject? Due(DateOnly? date, TimeOnly? time)
    {
        if (date is not { } d)
            return null;

        var value = time is { } t
            ? d.ToDateTime(t).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new JsonObject { ["date"] = value };
    }

    /// <summary>
    /// A due date written out rather than picked — "every Monday", "in 3 days". The server reads it
    /// and sends back the schedule it settled on, which is the only way a recurrence can be set:
    /// there is no field that says "repeat weekly", just the words.
    /// </summary>
    public static JsonObject DueString(string text) => new() { ["string"] = text.Trim() };

    /// <summary>
    /// The fields that may be sent when recreating a task, so replaying a deleted one never echoes
    /// back server-owned values like <c>user_id</c> or <c>added_at</c>.
    /// </summary>
    public static JsonObject ForRecreate(JsonObject prior)
    {
        string[] writable =
            ["content", "description", "project_id", "section_id", "parent_id", "child_order", "priority", "due", "deadline", "labels", "duration"];

        var fields = new JsonObject();
        foreach (var key in writable)
            if (prior.TryGetPropertyValue(key, out var value) && value is not null)
                fields[key] = value.DeepClone();
        return fields;
    }
}
