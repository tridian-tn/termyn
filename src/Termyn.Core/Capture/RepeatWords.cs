using System.Globalization;
using System.Text;

namespace Termyn.Core.Capture;

/// <summary>
/// A <see cref="RepeatRule"/> written as the words Todoist reads, and those words read back.
/// </summary>
/// <remarks>
/// <para>
/// Only the forms Todoist's help on recurring dates gives: <c>every day</c>, <c>every 2 weeks
/// starting 3 January</c>, <c>every mon, fri at 20:00</c>, <c>every weekday</c>, <c>every 14
/// jan</c>, <c>every! 3 months</c>, <c>everyday until aug 3</c>. English only, since that's the
/// only language there are words for here.
/// </para>
/// <para>
/// Reading is the same forms the other way, and nothing more. Todoist reads a great deal this
/// doesn't — "every other fri", "every last workday", a repeat in German — and those read as
/// nothing here, so a caller shows them as words it can't take apart rather than guessing at them.
/// </para>
/// </remarks>
public static class RepeatWords
{
    private static readonly string[] ShortDays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private static readonly string[] LongDays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private static readonly DayOfWeek[] Workweek =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>The words that end the part of a repeat saying how often, and start one of its suffixes.</summary>
    private static readonly string[] Suffixes = ["at", "starting", "from", "until", "ending"];

    /// <summary>
    /// Writes a repeat as the words Todoist reads.
    /// </summary>
    /// <remarks>
    /// The start is written only where the words alone would begin somewhere else: "every sat"
    /// already starts on the coming Saturday, and saying so again only makes the words longer.
    /// </remarks>
    /// <param name="rule">The repeat</param>
    /// <param name="today">Today in the account's timezone, which the words are counted from</param>
    /// <returns>The words</returns>
    public static string Write(RepeatRule rule, DateOnly today)
    {
        var words = new StringBuilder(rule.FromCompletion ? "every!" : "every");
        DateOnly natural;

        if (rule.OnWeekdays)
        {
            var days = Ordered(rule.Days);
            words.Append(' ').Append(days.SequenceEqual(Workweek) ? "weekday" : string.Join(", ", days.Select(d => ShortDays[(int)d])));
            natural = NextOn(today, days);
        }
        else if (rule is { Unit: RepeatUnit.Month, Interval: 1, FromCompletion: false, Start: { } monthly })
        {
            words.Append(' ').Append(Ordinal(monthly.Day));
            natural = NextMonthDay(today, monthly.Day);
        }
        else if (rule is { Unit: RepeatUnit.Year, Interval: 1, FromCompletion: false, Start: { } yearly })
        {
            words.Append(' ').Append(yearly.Day.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Months[yearly.Month - 1]);
            natural = NextYearDay(today, yearly.Month, yearly.Day);
        }
        else
        {
            words.Append(' ');
            if (rule.Interval > 1)
                words.Append(rule.Interval.ToString(CultureInfo.InvariantCulture)).Append(' ');

            words.Append(UnitWord(rule.Unit));
            if (rule.Interval > 1)
                words.Append('s');

            natural = today;
        }

        if (rule.Time is { } time)
            words.Append(" at ").Append(time.ToString("HH:mm", CultureInfo.InvariantCulture));

        if (rule.Start is { } start && start != natural)
            words.Append(" starting ").Append(Date(start));

        if (rule.Until is { } until)
            words.Append(" until ").Append(Date(until));

        return words.ToString();
    }

    /// <summary>
    /// Reads words as a repeat, if they're one of the forms <see cref="Write"/> writes.
    /// </summary>
    /// <param name="text">The words</param>
    /// <param name="today">Today in the account's timezone, which the words are counted from</param>
    /// <returns>The repeat, or null when the words aren't one this can take apart</returns>
    public static RepeatRule? Read(string? text, DateOnly today)
    {
        var tokens = (text ?? string.Empty)
            .ToLowerInvariant()
            .Replace(",", " ", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t != "and")
            .ToList();

        if (tokens.Count == 0)
            return null;

        // The one-word forms, and "everyday" run together, as the same thing written out.
        switch (tokens[0])
        {
            case "everyday":
                tokens.RemoveAt(0);
                tokens.InsertRange(0, ["every", "day"]);
                break;
            case "daily" or "weekly" or "monthly" or "yearly" or "annually":
                var unit = tokens[0] switch { "daily" => "day", "weekly" => "week", "monthly" => "month", _ => "year" };
                tokens.RemoveAt(0);
                tokens.InsertRange(0, ["every", unit]);
                break;
        }

        var fromCompletion = tokens[0] == "every!";
        if (tokens[0] is not ("every" or "every!"))
            return null;

        var end = tokens.FindIndex(1, t => Suffixes.Contains(t));
        var core = tokens.GetRange(1, (end < 0 ? tokens.Count : end) - 1);
        var rule = Core(core, today, fromCompletion);
        if (rule is null)
            return null;

        // The suffixes, in whatever order they come.
        var at = end < 0 ? tokens.Count : end;
        while (at < tokens.Count)
        {
            var word = tokens[at];
            var next = tokens.FindIndex(at + 1, t => Suffixes.Contains(t));
            var part = tokens.GetRange(at + 1, (next < 0 ? tokens.Count : next) - at - 1);
            at = next < 0 ? tokens.Count : next;

            switch (word)
            {
                case "at" when TimeOf(part) is { } time:
                    rule = rule with { Time = time };
                    break;
                case "starting" or "from" when DayOf(part, today) is { } start:
                    rule = rule with { Start = start };
                    break;
                case "until" or "ending" when DayOf(part, today) is { } until:
                    rule = rule with { Until = until };
                    break;
                default:
                    return null;
            }
        }

        return rule;
    }

    /// <summary>The part of a repeat that says how often, read as a repeat with nothing else said.</summary>
    /// <param name="core">The words between <c>every</c> and the first suffix</param>
    /// <param name="today">Today</param>
    /// <param name="fromCompletion">Whether it was <c>every!</c></param>
    /// <returns>The repeat, or null when the words aren't one</returns>
    private static RepeatRule? Core(List<string> core, DateOnly today, bool fromCompletion)
    {
        if (core.Count == 0)
            return null;

        // Days of the week, or the five working ones, which only a plain "every" names.
        if (!fromCompletion && core is ["weekday"] or ["workday"])
            return new RepeatRule(RepeatUnit.Week, Weekdays: Workweek);

        if (!fromCompletion && core.Select(DayNamed).ToList() is var named && named.All(d => d is not null))
            return new RepeatRule(RepeatUnit.Week, Weekdays: Ordered(named.Select(d => d!.Value)), Start: NextOn(today, named.Select(d => d!.Value)));

        // A day of the month, as "24th" or "24", and a day of the year, either way round.
        if (!fromCompletion && core.Count == 1 && MonthDay(core[0]) is { } dayOfMonth)
            return new RepeatRule(RepeatUnit.Month, Start: NextMonthDay(today, dayOfMonth));

        if (!fromCompletion && core.Count == 2 && YearDay(core[0], core[1]) is var (month, day))
            return new RepeatRule(RepeatUnit.Year, Start: NextYearDay(today, month, day));

        // Every so many of a unit: "day", "2 weeks", "other month", "quarter".
        var interval = 1;
        var plural = false;
        var rest = core;

        if (core.Count == 2 && core[0] == "other")
        {
            interval = 2;
            rest = core.GetRange(1, 1);
        }
        else if (core.Count == 2 && int.TryParse(core[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 999)
        {
            interval = count;
            plural = count > 1;
            rest = core.GetRange(1, 1);
        }

        if (rest.Count != 1)
            return null;

        if (rest[0] == "quarter" && interval == 1)
            return new RepeatRule(RepeatUnit.Month, 3, FromCompletion: fromCompletion);

        RepeatUnit? unit = rest[0].TrimEnd('s') switch
        {
            "day" => RepeatUnit.Day,
            "week" => RepeatUnit.Week,
            "month" => RepeatUnit.Month,
            "year" => RepeatUnit.Year,
            _ => null,
        };

        // "every 2 week" isn't English, and "every days" isn't either; "every other week" is.
        if (unit is null || rest[0].EndsWith('s') != plural)
            return null;

        return new RepeatRule(unit.Value, interval, FromCompletion: fromCompletion);
    }

    /// <summary>The singular word for a unit.</summary>
    private static string UnitWord(RepeatUnit unit) => unit switch
    {
        RepeatUnit.Day => "day",
        RepeatUnit.Week => "week",
        RepeatUnit.Month => "month",
        _ => "year",
    };

    /// <summary>Days of the week once each, Monday first, the way a week is read aloud.</summary>
    private static List<DayOfWeek> Ordered(IEnumerable<DayOfWeek> days)
        => days.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();

    /// <summary>The first of some days of the week on or after today.</summary>
    private static DateOnly NextOn(DateOnly today, IEnumerable<DayOfWeek> days)
    {
        var wanted = days.ToHashSet();
        var day = today;
        while (!wanted.Contains(day.DayOfWeek))
            day = day.AddDays(1);

        return day;
    }

    /// <summary>The first day on or after today that's a given day of its month, skipping months too short for it.</summary>
    private static DateOnly NextMonthDay(DateOnly today, int dayOfMonth)
    {
        var month = new DateOnly(today.Year, today.Month, 1);
        while (true)
        {
            if (dayOfMonth <= DateTime.DaysInMonth(month.Year, month.Month)
                && new DateOnly(month.Year, month.Month, dayOfMonth) is var day && day >= today)
            {
                return day;
            }

            month = month.AddMonths(1);
        }
    }

    /// <summary>The first day on or after today that's a given day of the year, skipping years without it.</summary>
    private static DateOnly NextYearDay(DateOnly today, int month, int day)
    {
        for (var year = today.Year; ; year++)
        {
            if (day <= DateTime.DaysInMonth(year, month) && new DateOnly(year, month, day) is var date && date >= today)
                return date;
        }
    }

    /// <summary>A day of the month as Todoist writes one: "1st", "2nd", "24th".</summary>
    private static string Ordinal(int day)
    {
        var suffix = (day % 100) is 11 or 12 or 13
            ? "th"
            : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

        return day.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>A day written for Todoist to read, with its year so it can't be taken for next year's.</summary>
    private static string Date(DateOnly day) => QuickAddParser.Written(day, null, english: true);

    /// <summary>A day of the week by its name, in full or shortened.</summary>
    private static DayOfWeek? DayNamed(string word)
    {
        var at = Array.IndexOf(ShortDays, word);
        if (at < 0)
            at = Array.IndexOf(LongDays, word);

        return at < 0 ? null : (DayOfWeek)at;
    }

    /// <summary>A day of the month, as "24th" or "24".</summary>
    private static int? MonthDay(string word)
    {
        var figures = word.Length > 2 && word[^2..] is "st" or "nd" or "rd" or "th" ? word[..^2] : word;

        return int.TryParse(figures, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 1 and <= 31
            ? day
            : null;
    }

    /// <summary>A month by its name, in full or shortened, as one to twelve.</summary>
    private static int? MonthNamed(string word)
        => word.Length >= 3 && Array.FindIndex(Months, m => word.StartsWith(m, StringComparison.Ordinal)) is var at and >= 0
           && CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[at].ToLowerInvariant() is var full
           && (word == Months[at] || word == full)
            ? at + 1
            : null;

    /// <summary>A day of the year written as "24 oct" or "oct 24".</summary>
    private static (int Month, int Day)? YearDay(string first, string second)
    {
        if (MonthDay(first) is { } day && MonthNamed(second) is { } month && day <= DateTime.DaysInMonth(2024, month))
            return (month, day);

        if (MonthNamed(first) is { } before && MonthDay(second) is { } after && after <= DateTime.DaysInMonth(2024, before))
            return (before, after);

        return null;
    }

    /// <summary>A day written after "starting" or "until": "3 jan 2027", "3 jan", "jan 3", or "2027-01-03".</summary>
    private static DateOnly? DayOf(List<string> words, DateOnly today)
    {
        if (words is [var iso]
            && DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var figures))
        {
            return figures;
        }

        if (words.Count is < 2 or > 3 || YearDay(words[0], words[1]) is not var (month, day))
            return null;

        if (words.Count == 2)
            return NextYearDay(today, month, day);

        return int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
               && year is >= 1 and <= 9999
               && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }

    /// <summary>A time written after "at": "09:00", "9:30", "9am", "9:30pm", or "noon".</summary>
    private static TimeOnly? TimeOf(List<string> words)
    {
        if (words is not [var word])
            return null;

        if (word == "noon")
            return new TimeOnly(12, 0);

        var half = word.EndsWith("am", StringComparison.Ordinal) ? 0 : word.EndsWith("pm", StringComparison.Ordinal) ? 12 : -1;
        var figures = half >= 0 ? word[..^2] : word;
        var parts = figures.Split(':');

        if (parts.Length > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || (parts.Length == 2 && (parts[1].Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))))
        {
            return null;
        }

        var minute = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;

        // A bare hour with no colon and no am or pm is a count rather than a time.
        if (half < 0 && parts.Length == 1)
            return null;

        if (half >= 0)
        {
            if (hour is < 1 or > 12)
                return null;

            hour = hour % 12 + half;
        }

        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59 ? new TimeOnly(hour, minute) : null;
    }
}
