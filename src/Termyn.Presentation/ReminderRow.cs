using Termyn.Core.Model;

namespace Termyn.Presentation;

/// <summary>
/// A reminder on a task, as the list of them says it in words.
/// </summary>
/// <remarks>
/// A list shows these by <see cref="ToString"/>, so the wording is here, where it can be tested,
/// rather than in the dialog that lists them.
/// </remarks>
/// <param name="Reminder">The reminder it stands for</param>
public sealed record ReminderRow(Reminder Reminder)
{
    /// <summary>
    /// Whether Termyn could put this reminder back if it were removed. A kind it can't author is a
    /// one-way door, so it isn't offered.
    /// </summary>
    public bool CanRemove => Reminder.Kind is ReminderKind.Relative or ReminderKind.Absolute;

    public override string ToString() => Reminder.Kind switch
    {
        ReminderKind.Absolute => $"At {Moment(Reminder.DueDate)}",
        ReminderKind.Location => $"At {Reminder.LocationName ?? "a place"} (set in Todoist)",
        ReminderKind.Unknown => "A reminder set in Todoist",
        _ => Reminder.MinuteOffset == 0
            ? "When it's due"
            : $"{Describe(Reminder.MinuteOffset)} before it's due",
    };

    /// <summary>
    /// An absolute reminder's moment, in words rather than the timestamp the server sent, so it
    /// sits beside the relative ones instead of standing out as raw data.
    /// </summary>
    private static string Moment(string? due)
        => DateTime.TryParse(due, out var when) ? when.ToString("ddd d MMM, HH:mm") : due ?? "a set time";

    /// <summary>
    /// Offsets aren't limited to the ones the dialog offers — the web app sets whatever it likes —
    /// so the odd sizes have to read properly too.
    /// </summary>
    private static string Describe(int minutes) => minutes switch
    {
        < 60 => Plural(minutes, "minute"),
        < 1440 when minutes % 60 == 0 => Plural(minutes / 60, "hour"),
        _ when minutes % 1440 == 0 => Plural(minutes / 1440, "day"),
        _ => Plural(minutes, "minute"),
    };

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")}";
}
