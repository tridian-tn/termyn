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
    /// <param name="parentId">
    /// The task this one goes under, for a sub-task. Null for one that stands on its own
    /// </param>
    public static JsonObject ForAdd(
        QuickAddParse parse,
        string? projectId = null,
        string? sectionId = null,
        string? parentId = null)
    {
        var fields = new JsonObject { ["content"] = parse.Content };

        if (projectId is not null)
            fields["project_id"] = projectId;

        if (sectionId is not null)
            fields["section_id"] = sectionId;

        if (parentId is not null)
            fields["parent_id"] = parentId;

        if (parse.Labels.Count > 0)
        {
            var labels = new JsonArray();
            foreach (var label in parse.Labels)
                labels.Add(label);
            fields["labels"] = labels;
        }

        if (parse.Priority != Priority.P4)
            fields["priority"] = PriorityMap.ToApi(parse.Priority);

        // A recurrence goes over as no due date at all rather than a wrong one. A priority ends the
        // recurrence run, so "every day p1 9am" leaves a bare time behind that the parser reads as
        // this morning — filing a repeating task as a one-off due today.
        if (!parse.IsRecurrence && Due(parse.DueDate, parse.DueTime) is { } due)
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
    /// A task's <c>due</c> moved to another day, with everything else about it left as it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What Todoist's own Reschedule does, and what its "Tomorrow" and "Next week" do: the day
    /// changes and nothing else does. A time stays on the new day, and a repeat stays a repeat — the
    /// date on a recurring due is the occurrence it's on, so moving that is moving this one, and the
    /// words go back with it so the server doesn't take a bare date as the end of the repeat.
    /// </para>
    /// <para>
    /// A one-off goes without its words. They're whatever was typed when it was set, and "tomorrow"
    /// sent back today would have the server read a different day from the one asked for.
    /// </para>
    /// <para>
    /// A due with a fixed timezone is a UTC instant, and its time is the time in that zone rather
    /// than in UTC, so it's moved there: in UTC the day can be one out either side of midnight.
    /// </para>
    /// </remarks>
    /// <param name="due">The task's <c>due</c> as it stands, or null when it has none</param>
    /// <param name="day">The day to move it to, in the account's timezone</param>
    /// <param name="zone">The account's timezone, for a fixed one that can't be found</param>
    /// <returns>The <c>due</c> to send</returns>
    public static JsonObject Rescheduled(JsonObject? due, DateOnly day, TimeZoneInfo zone)
    {
        var stamp = due?["date"] is JsonValue value && value.TryGetValue(out string? held) ? held : null;
        var moved = new JsonObject { ["date"] = MovedStamp(stamp, day, Zone(due, zone)) };

        if (due?["timezone"] is JsonValue fixedZone && fixedZone.TryGetValue(out string? zoneName) && zoneName.Length > 0)
            moved["timezone"] = zoneName;

        if (due?["is_recurring"] is JsonValue recurring && recurring.TryGetValue(out bool repeats) && repeats
            && due["string"] is JsonValue words && words.TryGetValue(out string? text) && text.Length > 0)
        {
            moved["string"] = text;
            moved["is_recurring"] = true;

            if (due["lang"] is JsonValue lang && lang.TryGetValue(out string? language) && language.Length > 0)
                moved["lang"] = language;
        }

        return moved;
    }

    /// <summary>
    /// A repeat's <c>due</c> set to start on a given day: the words that make it a repeat, and the
    /// day as the occurrence it's on.
    /// </summary>
    /// <param name="words">The repeat's words, as the server was last sent them</param>
    /// <param name="day">The day its first occurrence falls on</param>
    /// <param name="time">The time it's due at, or null for the whole day</param>
    /// <returns>The <c>due</c> to send</returns>
    public static JsonObject RepeatFrom(string words, DateOnly day, TimeOnly? time)
    {
        var due = Due(day, time)!;
        due["string"] = words;
        due["is_recurring"] = true;
        return due;
    }

    /// <summary>A due's date written for another day, keeping whatever time it had.</summary>
    /// <param name="stamp">The date as the server wrote it, or null when there isn't one</param>
    /// <param name="day">The day to move it to</param>
    /// <param name="zone">The zone a fixed time is read in</param>
    /// <returns>The new date, in the form the old one was in</returns>
    private static string MovedStamp(string? stamp, DateOnly day, TimeZoneInfo zone)
    {
        var whole = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (stamp is null || !stamp.Contains('T'))
            return whole;

        if (stamp.EndsWith('Z'))
        {
            if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
                return whole;

            var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
            var local = DateTime.SpecifyKind(day.ToDateTime(time), DateTimeKind.Unspecified);

            // An hour the clocks skip on the new day doesn't exist to convert, so it's the hour after.
            if (zone.IsInvalidTime(local))
                local = local.AddHours(1);

            return TimeZoneInfo.ConvertTimeToUtc(local, zone).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        return DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var floating)
            ? day.ToDateTime(TimeOnly.FromDateTime(floating)).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : whole;
    }

    /// <summary>The zone a due's time is kept in: its own fixed one if it has one, or the account's.</summary>
    /// <param name="due">The task's <c>due</c>, or null</param>
    /// <param name="account">The account's timezone</param>
    /// <returns>The zone</returns>
    private static TimeZoneInfo Zone(JsonObject? due, TimeZoneInfo account)
    {
        if (due?["timezone"] is not JsonValue value || !value.TryGetValue(out string? name) || name.Length == 0)
            return account;

        return TimeZoneInfo.TryFindSystemTimeZoneById(name, out var found) ? found : account;
    }

    /// <summary>
    /// Builds a <c>deadline</c> object, or <c>null</c> to clear the one a task has.
    /// </summary>
    /// <remarks>
    /// A plain calendar day and nothing else: a deadline carries no time, no timezone and no
    /// recurrence, and means the same day wherever it's read. The language the server tags it with
    /// on the way back isn't sent — it's Todoist's note of how it read the words, and these were
    /// never words.
    /// </remarks>
    /// <param name="date">The day the task has to be finished by, or null to clear it</param>
    /// <returns>The object to send as the task's <c>deadline</c>, or null to clear it</returns>
    public static JsonObject? Deadline(DateOnly? date) => date is { } day
        ? new JsonObject { ["date"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
        : null;

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
