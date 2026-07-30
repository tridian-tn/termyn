using System.Globalization;
using System.Text.RegularExpressions;
using Termyn.Core.Model;
using Termyn.Core.Platform;

namespace Termyn.Core.Capture;

/// <summary>
/// Parses quick-add text without the server. This is deliberately a strict subset of Todoist's
/// syntax — only the tokens listed here are recognised; anything else is left in the task content
/// verbatim. Recurrence, reminders and assignees are flagged rather than guessed at, because
/// getting them wrong offline would create the wrong task.
/// </summary>
/// <remarks>
/// Recognised: <c>#project</c>, <c>/section</c>, <c>@label</c>, <c>p1</c>–<c>p4</c>, the dates
/// <c>today</c>/<c>tomorrow</c>/<c>tom</c>/<c>yyyy-MM-dd</c>/full weekday names, and a time of day
/// (<c>16:30</c>, <c>4pm</c>, <c>4:30pm</c>). Weekday abbreviations are not recognised: "sat" and
/// "sun" are ordinary words, and silently turning them into a due date mangles the task text.
/// </remarks>
public sealed partial class QuickAddParser
{
    private static readonly string[] WeekdayNames =
        ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    private readonly IClock _clock;

    public QuickAddParser(IClock clock) => _clock = clock;

    public QuickAddParse Parse(string text)
    {
        var content = new List<string>();
        var labels = new List<string>();
        var unsupported = new List<string>();
        string? project = null;
        string? section = null;
        var priority = Priority.P4;
        DateOnly? date = null;
        TimeOnly? time = null;
        var recurrence = false;

        var tokens = (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            switch (token[0])
            {
                case '#' when token.Length > 1:
                    project ??= token[1..];
                    continue;
                case '/' when token.Length > 1:
                    section ??= token[1..];
                    continue;
                case '@' when token.Length > 1:
                    if (!labels.Contains(token[1..], StringComparer.OrdinalIgnoreCase))
                        labels.Add(token[1..]);
                    continue;
                case '+' when token.Length > 1:
                    // Assignees are out of scope; drop the token but tell the user it was ignored.
                    unsupported.Add(token);
                    continue;
                case '!' when token.Length > 1:
                    // Reminders need the server; keep the text and flag it.
                    unsupported.Add(token);
                    content.Add(token);
                    continue;
            }

            if (TryParsePriority(token, out var parsedPriority))
            {
                priority = parsedPriority;
                continue;
            }

            // Recurrence is resolved by the server, never guessed at here. The whole phrase stays in
            // the content and is skipped, so a weekday or time inside it can't become a due date.
            if (token.Equals("every", StringComparison.OrdinalIgnoreCase))
            {
                var run = tokens[i..]
                    .TakeWhile(t => t[0] is not ('#' or '@' or '/' or '+') && !TryParsePriority(t, out _))
                    .ToArray();
                unsupported.Add(string.Join(' ', run));
                content.AddRange(run);
                recurrence = true;
                i += run.Length - 1;
                continue;
            }

            if (date is null && TryParseDate(token, out var parsedDate))
            {
                date = parsedDate;
                continue;
            }

            if (time is null && TryParseTime(token, out var parsedTime))
            {
                time = parsedTime;
                continue;
            }

            content.Add(token);
        }

        // A bare time of day means today.
        if (time is not null && date is null)
            date = _clock.Today;

        return new QuickAddParse
        {
            Content = string.Join(' ', content),
            ProjectName = project,
            SectionName = section,
            Labels = labels,
            Priority = priority,
            DueDate = date,
            DueTime = time,
            Unsupported = unsupported,
            IsRecurrence = recurrence,
        };
    }

    private static bool TryParsePriority(string token, out Priority priority)
    {
        priority = Priority.P4;
        if (token.Length != 2 || (token[0] != 'p' && token[0] != 'P'))
            return false;
        if (token[1] is < '1' or > '4')
            return false;
        priority = (Priority)(token[1] - '0');
        return true;
    }

    private bool TryParseDate(string token, out DateOnly date)
    {
        var lower = token.ToLowerInvariant();

        switch (lower)
        {
            case "today":
                date = _clock.Today;
                return true;
            case "tomorrow" or "tom":
                date = _clock.Today.AddDays(1);
                return true;
        }

        if (DateOnly.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        var weekday = Array.IndexOf(WeekdayNames, lower);
        if (weekday >= 0)
        {
            // The coming occurrence, counting today when it already matches.
            var today = _clock.Today;
            var offset = ((weekday + 1) - (int)today.DayOfWeek + 7) % 7;
            date = today.AddDays(offset);
            return true;
        }

        date = default;
        return false;
    }

    private static bool TryParseTime(string token, out TimeOnly time)
    {
        var match = TimePattern().Match(token);
        if (!match.Success)
        {
            time = default;
            return false;
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = match.Groups["ap"].Value.ToLowerInvariant();

        if (meridiem.Length > 0)
        {
            if (hour is < 1 or > 12)
            {
                time = default;
                return false;
            }
            hour = meridiem == "am" ? hour % 12 : hour % 12 + 12;
        }
        else if (!match.Groups["m"].Success)
        {
            // A bare number is a word, not a time: "buy 4 apples".
            time = default;
            return false;
        }

        if (hour > 23 || minute > 59)
        {
            time = default;
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    [GeneratedRegex(@"^(?<h>\d{1,2})(:(?<m>\d{2}))?(?<ap>am|pm|AM|PM)?$")]
    private static partial Regex TimePattern();
}
