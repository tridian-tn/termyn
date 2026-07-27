using System.Text.Json.Nodes;

namespace Termyn.Core.Model;

/// <summary>
/// Maps raw resource JSON to the typed views the UI consumes. The JSON remains authoritative;
/// these projections read only the fields Termyn displays and never mutate the source.
/// </summary>
public static class Projections
{
    public static TaskItem ToTaskItem(JsonObject o)
    {
        var due = o["due"] as JsonObject;
        return new TaskItem
        {
            Id = JsonRead.String(o, "id") ?? string.Empty,
            Content = JsonRead.String(o, "content") ?? string.Empty,
            ProjectId = JsonRead.String(o, "project_id"),
            SectionId = JsonRead.String(o, "section_id"),
            ParentId = JsonRead.String(o, "parent_id"),
            ChildOrder = JsonRead.Int(o, "child_order"),
            Priority = PriorityMap.FromApi(JsonRead.Int(o, "priority")),
            Labels = ReadLabels(o),
            Completed = JsonRead.Bool(o, "checked"),
            DueDate = due is null ? null : JsonRead.String(due, "date"),
            DueText = due is null ? null : JsonRead.String(due, "string"),
        };
    }

    public static Project ToProject(JsonObject o) => new()
    {
        Id = JsonRead.String(o, "id") ?? string.Empty,
        Name = JsonRead.String(o, "name") ?? string.Empty,
        ParentId = JsonRead.String(o, "parent_id"),
        // Todoist has used both field names across API versions; accept either.
        IsInboxProject = JsonRead.Bool(o, "is_inbox_project") || JsonRead.Bool(o, "inbox_project"),
        IsFavorite = JsonRead.Bool(o, "is_favorite"),
        ChildOrder = JsonRead.Int(o, "child_order"),
    };

    /// <summary>
    /// Reads the account's timezone name. Todoist reports it under <c>tz_info</c>, and the client
    /// falls back to the machine's own zone when it is missing or unrecognised.
    /// </summary>
    public static TimeZoneInfo ToTimeZone(JsonObject? user)
    {
        var name = user is null ? null : JsonRead.String(user["tz_info"] as JsonObject ?? user, "timezone");
        if (name is null)
            return TimeZoneInfo.Local;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    public static Section ToSection(JsonObject o) => new()
    {
        Id = JsonRead.String(o, "id") ?? string.Empty,
        Name = JsonRead.String(o, "name") ?? string.Empty,
        ProjectId = JsonRead.String(o, "project_id"),
    };

    private static IReadOnlyList<string> ReadLabels(JsonObject o)
    {
        if (o["labels"] is not JsonArray array || array.Count == 0)
            return [];

        var labels = new List<string>(array.Count);
        foreach (var node in array)
            if (node is JsonValue v)
                labels.Add(v.ToString());
        return labels;
    }
}
