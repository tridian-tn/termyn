using System.Globalization;
using System.Linq;
using Termyn.Core.Capture;
using Termyn.Core.Model;

namespace Termyn.Presentation;

/// <summary>
/// What a capture box says, before anything is typed and after. Shared by the main window and the
/// global one, which show the same thing — and here rather than in the app, because none of it needs
/// a window and all of it is user-facing wording.
/// </summary>
public static class CapturePreviewText
{
    /// <summary>
    /// What an empty box says it takes.
    /// </summary>
    /// <remarks>
    /// One string for both boxes. They were separate literals that happened to match, which is a
    /// thing that stays true only until somebody edits one of them.
    ///
    /// No day for an account that reads its dates in another language, or that's turned smart date
    /// recognition off. A capture read offline reads none for either, and with recognition off
    /// Todoist reads none online either, so a "tomorrow" typed from the hint ends up in the title.
    /// </remarks>
    /// <param name="readsDays">Whether a capture reads a day out of what's typed</param>
    /// <returns>The hint</returns>
    public static string Hint(bool readsDays)
        => readsDays ? Strings.CaptureHintWithDay : Strings.CaptureHint;

    /// <summary>Renders what the local parser made of some capture text, for the line under the box.</summary>
    public static string For(CapturePreview preview)
    {
        var parse = preview.Parse;
        var parts = new List<string> { $"\"{parse.Content}\"" };

        if (parse.ProjectName is { } project)
            parts.Add(preview.ProjectResolved ? "#" + project : string.Format(CultureInfo.CurrentCulture, Strings.CaptureProjectUnknown, project));
        if (parse.SectionName is { } section)
            parts.Add(preview.SectionResolved ? "/" + section : string.Format(CultureInfo.CurrentCulture, Strings.CaptureSectionUnknown, section));
        foreach (var label in parse.Labels)
            parts.Add("@" + label);
        if (parse.Priority != Priority.P4)
            parts.Add(parse.Priority.ToString());
        // A repeating task goes over with no due date at all, so naming one here would promise a
        // task that isn't the one about to be created. The schedule itself is named below, which is
        // the honest answer: the server settles when it next falls due, and nothing here can.
        if (!parse.IsRecurrence && parse.DueDate is { } date)
            parts.Add(parse.DueTime is { } time ? $"{date:yyyy-MM-dd} {time:HH:mm}" : $"{date:yyyy-MM-dd}");
        // A reminder the plan takes is named as one it will set; one the plan won't is named as a
        // reminder it can't, never silently, since the "!" was read and then dropped. Not for a
        // repeating capture, which is refused whole — naming a reminder would promise one on a task
        // that isn't created, the same reason the due date above goes unsaid.
        if (!parse.IsRecurrence && parse.Reminders.Count > 0)
        {
            var when = string.Join(", ", parse.Reminders.Select(When));
            parts.Add(string.Format(
                CultureInfo.CurrentCulture,
                preview.RemindersAvailable ? Strings.CaptureWillRemind : Strings.CaptureReminderNoPlan,
                when));
        }
        if (parse.Unsupported.Count > 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.CaptureNeedsConnection, string.Join(", ", parse.Unsupported)));

        return string.Join("  ·  ", parts);
    }

    /// <summary>A reminder in the terse words the preview line uses, matching its due date.</summary>
    /// <param name="reminder">The reminder the "!" named</param>
    /// <returns>The words</returns>
    private static string When(CaptureReminder reminder) => reminder switch
    {
        AbsoluteReminder a => $"{a.Date:yyyy-MM-dd} {a.Time:HH:mm}",
        RelativeReminder { MinutesBefore: 0 } => "when due",
        RelativeReminder { MinutesBefore: var m } when m % 60 == 0 => $"{m / 60}h before",
        RelativeReminder { MinutesBefore: var m } => $"{m}m before",
        _ => string.Empty,
    };
}
