using System.Globalization;
using System.Text.Json.Nodes;
using Termyn.Core.Model;

namespace Termyn.Core.Capture;

/// <summary>Builds the field sets Termyn sends when creating or editing a task.</summary>
public static class ItemFields
{
    /// <summary>
    /// Turns a locally parsed capture into <c>item_add</c> fields. Project and section names are
    /// resolved through <paramref name="resolveProjectId"/>; an unknown name is left unset so the
    /// task lands in the Inbox rather than being attached to the wrong project.
    /// </summary>
    public static JsonObject ForAdd(QuickAddParse parse, Func<string, string?>? resolveProjectId = null)
    {
        var fields = new JsonObject { ["content"] = parse.Content };

        if (parse.ProjectName is { } projectName && resolveProjectId?.Invoke(projectName) is { } projectId)
            fields["project_id"] = projectId;

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
}
