using System.Text.Json.Nodes;
using Termyn.Core.Capture;

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
        var deadline = o["deadline"] as JsonObject;
        return new TaskItem
        {
            Id = JsonRead.String(o, "id") ?? string.Empty,
            Content = JsonRead.String(o, "content") ?? string.Empty,
            Description = JsonRead.String(o, "description") ?? string.Empty,
            ProjectId = JsonRead.String(o, "project_id"),
            SectionId = JsonRead.String(o, "section_id"),
            ParentId = JsonRead.String(o, "parent_id"),
            ChildOrder = JsonRead.Int(o, "child_order"),
            Priority = PriorityMap.FromApi(JsonRead.Int(o, "priority")),
            Labels = ReadLabels(o),
            Completed = JsonRead.Bool(o, "checked"),
            CompletedAt = JsonRead.String(o, "completed_at"),

            // Todoist has used both field names across API versions; accept either.
            AddedAt = JsonRead.String(o, "added_at") ?? JsonRead.String(o, "date_added"),

            ResponsibleUid = JsonRead.String(o, "responsible_uid"),
            AssignedByUid = JsonRead.String(o, "assigned_by_uid"),
            AddedByUid = JsonRead.String(o, "added_by_uid"),
            DueDate = due is null ? null : JsonRead.String(due, "date"),
            Deadline = deadline is null ? null : JsonRead.String(deadline, "date"),
            DueText = due is null ? null : JsonRead.String(due, "string"),
            IsRecurring = due is not null && JsonRead.Bool(due, "is_recurring"),
        };
    }

    public static Project ToProject(JsonObject o) => new()
    {
        Id = JsonRead.String(o, "id") ?? string.Empty,
        Name = JsonRead.String(o, "name") ?? string.Empty,
        Description = JsonRead.String(o, "description") ?? string.Empty,
        ParentId = JsonRead.String(o, "parent_id"),
        // Todoist has used both field names across API versions; accept either.
        IsInboxProject = JsonRead.Bool(o, "is_inbox_project") || JsonRead.Bool(o, "inbox_project"),
        IsFavorite = JsonRead.Bool(o, "is_favorite"),
        IsArchived = JsonRead.Bool(o, "is_archived"),
        IsShared = JsonRead.Bool(o, "is_shared"),
        ChildOrder = JsonRead.Int(o, "child_order"),
        Color = JsonRead.String(o, "color"),
    };

    /// <summary>
    /// Reads the account's own user id, which is the whole of what "me" means in a filter.
    /// </summary>
    /// <remarks>
    /// Without it "assigned to: me" has nobody to be. Null where the user resource hasn't arrived
    /// yet, which is a first start before the first sync — and a term that can't be answered is
    /// refused rather than guessed at, so the filter says so instead of quietly matching nothing.
    /// </remarks>
    /// <param name="user">The user resource, or null when it hasn't been synced</param>
    /// <returns>The id, or null when there isn't one to give</returns>
    public static string? ToUserId(JsonObject? user)
        => user is null ? null : JsonRead.String(user, "id");

    /// <summary>
    /// Reads the day the account calls "next week".
    /// </summary>
    /// <remarks>
    /// The setting behind Todoist's own "next week" — the day a task lands on when it's put off that
    /// far — and the day the filter term of the same name resolves to. Todoist numbers the week from
    /// Monday, which is one off <see cref="DayOfWeek"/>'s own count from Sunday.
    ///
    /// Null when the account hasn't said, and no default stands in for it: Monday would be right for
    /// most accounts and quietly wrong for the rest, which is a filter answering with the wrong week
    /// rather than admitting it can't answer.
    /// </remarks>
    /// <param name="user">The user resource, or null when it hasn't been synced</param>
    /// <returns>The day, or null when there isn't one to give</returns>
    public static DayOfWeek? ToNextWeek(JsonObject? user)
    {
        if (user is null)
            return null;

        return Weekday(JsonRead.Int(user, "next_week"));
    }

    /// <summary>
    /// Reads what the account says about reading a typed date.
    /// </summary>
    /// <remarks>
    /// <c>date_format</c> is 0 for day-first and 1 for month-first, so a missing one mustn't read
    /// as the nought it would default to: that would be a day-first account that never said so.
    ///
    /// Smart date recognition being off is <c>dateist_inline_disabled</c> in <c>features</c>, the
    /// one place Todoist's reference puts it. It's off only when the account says so: Todoist reads
    /// dates in a title unless it's told not to.
    /// </remarks>
    /// <param name="user">The user resource, or null when it hasn't been synced</param>
    /// <returns>The settings, with null wherever the account hasn't given one</returns>
    public static DateSettings ToDateSettings(JsonObject? user)
    {
        if (user is null)
            return DateSettings.Unknown;

        return new DateSettings(
            ToTimeZone(user),
            JsonRead.IntOrNull(user, "date_format") switch
            {
                0 => true,
                1 => false,
                _ => null,
            },
            Weekday(JsonRead.Int(user, "start_day")),
            ToNextWeek(user),
            ToDateLanguage(user),
            user["features"] is JsonObject features && JsonRead.Bool(features, "dateist_inline_disabled"));
    }

    /// <summary>
    /// Reads the language the account reads a typed date in.
    /// </summary>
    /// <remarks>
    /// <c>dateist_lang</c> when the account has set one apart from its own language, and
    /// <c>lang</c> when it hasn't. Todoist's reference lists <c>dateist_lang</c> on the user itself
    /// while its example carries it in <c>features</c>, so it's looked for in both.
    ///
    /// Only a string counts. Anything else there is something other than a language, and taken for
    /// one it would stop every English word being read for an account that reads English.
    /// </remarks>
    /// <param name="user">The user resource, or null when it hasn't been synced</param>
    /// <returns>The language as Todoist names it, or null when there isn't one to give</returns>
    public static string? ToDateLanguage(JsonObject? user)
    {
        if (user is null)
            return null;

        return Language(user, "dateist_lang")
               ?? (user["features"] is JsonObject features ? Language(features, "dateist_lang") : null)
               ?? Language(user, "lang");

        static string? Language(JsonObject o, string key)
            => o[key] is JsonValue value && value.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : null;
    }

    /// <summary>
    /// A day of the week as Todoist numbers it, from Monday as 1 to Sunday as 7 — one off
    /// <see cref="DayOfWeek"/>'s own count from Sunday.
    /// </summary>
    /// <param name="day">The day as Todoist numbers it</param>
    /// <returns>The day, or null when the number isn't one</returns>
    private static DayOfWeek? Weekday(int day) => day is >= 1 and <= 7 ? (DayOfWeek)(day % 7) : null;

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
        Color = JsonRead.String(o, "color"),
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
            UploadLimitMb = JsonRead.Int(current, "upload_limit_mb"),
        };
    }

    public static Filter ToFilter(JsonObject o) => new()
    {
        Id = JsonRead.String(o, "id") ?? string.Empty,
        Name = JsonRead.String(o, "name") ?? string.Empty,
        Query = JsonRead.String(o, "query") ?? string.Empty,
        IsFavorite = JsonRead.Bool(o, "is_favorite"),
        ItemOrder = JsonRead.Int(o, "item_order"),
        Color = JsonRead.String(o, "color"),
    };

    public static Comment ToComment(JsonObject o) => new()
    {
        Id = JsonRead.String(o, "id") ?? string.Empty,
        ItemId = JsonRead.String(o, "item_id"),
        ProjectId = JsonRead.String(o, "project_id"),
        Content = JsonRead.String(o, "content") ?? string.Empty,
        PostedAt = JsonRead.String(o, "posted_at"),

        Attachment = o["file_attachment"] is JsonObject file ? ToAttachment(file) : null,
    };

    /// <summary>
    /// The file on a comment.
    /// </summary>
    /// <remarks>
    /// Todoist reports <c>upload_state</c> as <c>pending</c> or <c>completed</c> on a file it holds
    /// itself. Only an explicit <c>pending</c> counts as still processing: the field is absent
    /// altogether on an attachment that points at something outside Todoist, and reading "not
    /// completed" as "not ready" would leave those marked as processing for ever and refuse to open
    /// a file that was never going to be uploaded in the first place.
    /// </remarks>
    public static FileAttachment ToAttachment(JsonObject o) => new(
        JsonRead.String(o, "file_name") ?? string.Empty,
        JsonRead.Long(o, "file_size"),
        JsonRead.String(o, "file_type") ?? string.Empty,
        JsonRead.String(o, "file_url") ?? string.Empty,
        string.Equals(JsonRead.String(o, "upload_state"), "pending", StringComparison.OrdinalIgnoreCase));

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
