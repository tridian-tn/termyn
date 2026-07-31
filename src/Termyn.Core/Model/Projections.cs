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
            CompletedAt = JsonRead.String(o, "completed_at"),
            DueDate = due is null ? null : JsonRead.String(due, "date"),
            DueText = due is null ? null : JsonRead.String(due, "string"),
            IsRecurring = due is not null && JsonRead.Bool(due, "is_recurring"),
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
        IsArchived = JsonRead.Bool(o, "is_archived"),
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
        IsArchived = JsonRead.Bool(o, "is_archived"),
        SectionOrder = JsonRead.Int(o, "section_order"),
    };

    public static Label ToLabel(JsonObject o) => new()
    {
        Id = JsonRead.String(o, "id") ?? string.Empty,
        Name = JsonRead.String(o, "name") ?? string.Empty,
        IsFavorite = JsonRead.Bool(o, "is_favorite"),
        ItemOrder = JsonRead.Int(o, "item_order"),
    };

    public static Reminder ToReminder(JsonObject o)
    {
        var due = o["due"] as JsonObject;
        return new Reminder
        {
            Id = JsonRead.String(o, "id") ?? string.Empty,
            ItemId = JsonRead.String(o, "item_id"),
            Kind = JsonRead.String(o, "type") switch
            {
                "relative" or null => ReminderKind.Relative,
                "absolute" => ReminderKind.Absolute,
                "location" => ReminderKind.Location,
                _ => ReminderKind.Unknown,
            },
            MinuteOffset = JsonRead.Int(o, "minute_offset"),
            DueDate = due is null ? null : JsonRead.String(due, "date"),
            LocationName = JsonRead.String(o, "name"),
        };
    }

    /// <summary>
    /// Reads the plan's limits. The resource holds the current plan alongside the one it could be
    /// upgraded to, and only the current one says what this account may do today.
    /// </summary>
    public static PlanLimits ToPlanLimits(JsonObject o)
    {
        var current = o["current"] as JsonObject ?? o;
        return new PlanLimits
        {
            PlanName = JsonRead.String(current, "plan_name") ?? string.Empty,
            Reminders = JsonRead.Bool(current, "reminders"),
            MaxTimeReminders = JsonRead.Int(current, "max_reminders_time"),
        };
    }

    public static Filter ToFilter(JsonObject o) => new()
    {
        Id = JsonRead.String(o, "id") ?? string.Empty,
        Name = JsonRead.String(o, "name") ?? string.Empty,
        Query = JsonRead.String(o, "query") ?? string.Empty,
        IsFavorite = JsonRead.Bool(o, "is_favorite"),
        ItemOrder = JsonRead.Int(o, "item_order"),
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
