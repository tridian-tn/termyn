using System.Globalization;
using System.Text.RegularExpressions;
using Termyn.Core.Filters;
using Termyn.Core.Model;
using Termyn.Core.Platform;

namespace Termyn.Core.Capture;

/// <summary>
/// Parses quick-add text without the server, and reads a day typed into a box of its own. This is
/// deliberately a strict subset of Todoist's syntax — only the tokens listed here are recognised;
/// anything else is left in the task content verbatim. A task's recurrence and its assignees are
/// flagged rather than guessed at, because getting them wrong offline would create the wrong task.
/// </summary>
/// <remarks>
/// <para>
/// The one grammar for a day, wherever one is typed. A captured task, a due date and a deadline are
/// all read with it, so the same words can't name a day in one box and something else in the next.
/// And every form here is one Todoist reads the same way, by the same account settings: online, a
/// capture goes to the server as the words, so a phrase read differently here would give a task a
/// date that depended on whether the network was up. Todoist reads a good deal more than this, and
/// what it reads that this doesn't is left for the server rather than approximated.
/// </para>
/// <para>
/// Recognised: <c>#project</c>, <c>/section</c>, <c>@label</c> (with <c>\ </c> for a space in the
/// name, as Todoist writes it), <c>p1</c>–<c>p4</c>. Days:
/// <c>today</c>/<c>tod</c>, <c>tomorrow</c>/<c>tom</c>, a weekday's full name, <c>this</c> or
/// <c>next</c> and a weekday, <c>next week</c>/<c>month</c>/<c>year</c>, <c>in 3 days</c> (or weeks,
/// or months), <c>end of month</c>, a day and a month either way round with or without a year
/// (<c>4 aug</c>, <c>Aug 4th 2027</c>), and figures (<c>25/12</c>, <c>25/12/2026</c>,
/// <c>2026-12-25</c>, <c>2026/12/25</c>). Times: <c>16:30</c>, <c>4pm</c>, <c>4:30pm</c>, with or
/// without an <c>at</c> before them.
/// </para>
/// <para>
/// A weekday's abbreviation on its own isn't a day: "sat" and "sun" are ordinary words, and silently
/// turning them into a due date mangles the task text. After <c>this</c> or <c>next</c> it can't be
/// anything else, so it's read there.
/// </para>
/// <para>
/// The words are English, and Todoist reads a typed date in the account's own language. So for an
/// account that reads another one, a box asking for a day reads only figures — <c>25/12</c>,
/// <c>2026-12-25</c>, <c>16:30</c> — which mean the same in any language, and a capture reads no day
/// at all, since the words beside its figures may be ones that change them.
/// </para>
/// <para>
/// An account can also turn smart date recognition off, and Todoist then reads no date out of a
/// task's title, a repeat included. A capture for one reads none either, and leaves every word in
/// the title. A box asking for a day still reads one, as Todoist's scheduler does.
/// </para>
/// </remarks>
public sealed partial class QuickAddParser
{
    /// <summary>The days of the week, indexed the way <see cref="DayOfWeek"/> counts them.</summary>
    private static readonly string[] WeekdayNames =
        ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>The weekdays shortened, which only count as days after <c>this</c> or <c>next</c>.</summary>
    private static readonly Dictionary<string, DayOfWeek> WeekdaysShort = new()
    {
        ["sun"] = DayOfWeek.Sunday,
        ["mon"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday,
        ["tues"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday,
        ["thur"] = DayOfWeek.Thursday,
        ["thurs"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday,
    };

    /// <summary>The months, by their names in full and shortened.</summary>
    private static readonly Dictionary<string, int> Months = new()
    {
        ["january"] = 1, ["jan"] = 1,
        ["february"] = 2, ["feb"] = 2,
        ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["may"] = 5,
        ["june"] = 6, ["jun"] = 6,
        ["july"] = 7, ["jul"] = 7,
        ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sep"] = 9, ["sept"] = 9,
        ["october"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12,
    };

    /// <summary>A month by its name, in full or shortened.</summary>
    /// <param name="word">The word, lower-cased</param>
    /// <returns>The month as one to twelve, or null when the word doesn't name one</returns>
    internal static int? MonthNamed(string word) => Months.TryGetValue(word, out var month) ? month : null;

    /// <summary>
    /// The words a schedule tends to open with, beyond the <c>every</c> that marks one anywhere.
    /// Only counted when they open a date box: in a capture, "Write daily report" is a title.
    /// </summary>
    private static readonly string[] RepeatStarters = ["daily", "weekly", "monthly", "yearly", "annually", "each"];

    private readonly IClock _clock;

    public QuickAddParser(IClock clock) => _clock = clock;

    /// <summary>Parses quick-add text, knowing nothing of the account it's for.</summary>
    /// <param name="text">What was typed</param>
    /// <returns>What it was read as</returns>
    public QuickAddParse Parse(string text) => Parse(text, DateSettings.Unknown);

    /// <summary>Parses quick-add text, reading any day in it the way the account would.</summary>
    /// <param name="text">What was typed</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <returns>What it was read as</returns>
    public QuickAddParse Parse(string text, DateSettings settings)
    {
        var now = Now(settings);
        var content = new List<string>();
        var labels = new List<string>();
        var unsupported = new List<string>();
        string? project = null;
        string? section = null;
        var priority = Priority.P4;
        DateOnly? date = null;
        TimeOnly? time = null;
        var recurrence = false;
        var pending = new List<PendingReminder>();

        // Whether a day was passed over that Todoist would read, and where the time's words were
        // taken from, so they can go back if the time turns out to have no day it can go on.
        var declined = false;
        string[] timeWords = [];
        var timeStart = 0;
        var timeSlot = 0;

        // For an account that reads its dates in another language, a capture reads no day or time
        // at all. Figures mean the same in any language, but the words beside them needn't: "jedes
        // Jahr am 25/12" repeats and "25/12 um 16 Uhr" has a time, and neither is read here, so the
        // figures alone would give the task a day, or a day without its time, Todoist wouldn't. Nor
        // for one with smart date recognition off, where Todoist leaves every date in the title.
        var readsDays = settings.CaptureReadsDays;

        var tokens = CaptureTokens(text);

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            switch (token[0])
            {
                // The first one names it and a second is left in the words. Dropped, it went from a
                // task without being applied to it — the one thing here that lost what was typed.
                case '#' when token.Length > 1:
                    if (project is null)
                        project = Unescaped(token[1..]);
                    else
                        content.Add(token);
                    continue;
                case '/' when token.Length > 1:
                    if (section is null)
                        section = Unescaped(token[1..]);
                    else
                        content.Add(token);
                    continue;
                case '@' when token.Length > 1:
                    var label = Unescaped(token[1..]);
                    if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase))
                        labels.Add(label);
                    continue;
                case '+' when token.Length > 1:
                    // Assignees are out of scope; drop the token but tell the user it was ignored.
                    unsupported.Add(token);
                    continue;
                case '!' when token.Length > 1:
                {
                    if (token[1..].Equals("every", StringComparison.OrdinalIgnoreCase))
                    {
                        // A recurring reminder goes to the server as its words, for it to work out
                        // when it next fires. All that's read here is where those words end, and that
                        // has to be exact: Todoist refuses a schedule with a word of the title on it.
                        if (readsDays && RecurringReminderAt(tokens, i, settings, out var everyUsed) is { } schedule)
                        {
                            pending.Add(new StagedRecurring(schedule));
                            i += everyUsed - 1;
                            continue;
                        }

                        // One it can't read is the server's, like any repeat: consume the run so a
                        // trailing time can't become a due time, and flag it. Not a recurrence of the
                        // task itself, which repeats the task and would refuse the capture.
                        var run = new List<string> { token };
                        run.AddRange(tokens[(i + 1)..]
                            .TakeWhile(t => t[0] is not ('#' or '@' or '/' or '+' or '!') && !TryParsePriority(t, out _)));
                        unsupported.Add(string.Join(' ', run));
                        content.AddRange(run);
                        i += run.Count - 1;
                        continue;
                    }

                    // A reminder Todoist's apps read here is staged and set once the task has its id.
                    // Gated on the same setting a date is: for an account that reads no dates, whether
                    // "!" is read at all isn't something the docs settle, so it's left in the words.
                    if (readsDays && ReminderAt(tokens, i, now, settings, out var remUsed) is { } reminder)
                    {
                        pending.Add(reminder);
                        i += remUsed - 1;
                        continue;
                    }

                    // One this grammar can't read keeps its text and is flagged, as before.
                    unsupported.Add(token);
                    content.Add(token);
                    continue;
                }
            }

            if (TryParsePriority(token, out var parsedPriority))
            {
                priority = parsedPriority;
                continue;
            }

            // Recurrence is resolved by the server, never guessed at here. The whole phrase stays in
            // the content and is skipped, so a weekday or time inside it can't become a due date.
            // Not flagged with smart date recognition off, since the server wouldn't read it either,
            // and saying it needs a connection would promise a repeat that's never coming.
            if (!settings.InlineDatesOff && IsRepeatWord(token))
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

            if (readsDays && date is null)
            {
                if (DayAt(tokens, i, DateOnly.FromDateTime(now), settings, out var used, out var unread) is { } day)
                {
                    date = day;
                    i += used - 1;
                    continue;
                }

                // A day passed over goes into the words whole, so no part of it is read as a day of
                // its own: "Aug 27" out of "4 Aug 27", or this week's Friday out of "next friday".
                if (unread)
                {
                    declined = true;
                    content.AddRange(tokens[i..(i + used)]);
                    i += used - 1;
                    continue;
                }
            }

            if (readsDays && time is null && TimeAt(tokens, i, settings, out var took) is { } at)
            {
                time = at;
                timeWords = tokens[i..(i + took)];
                timeStart = i;
                timeSlot = content.Count;
                i += took - 1;
                continue;
            }

            content.Add(token);
        }

        // A time on its own is today's, or tomorrow's once it's gone — unless there's a day beside
        // it that wasn't read. "Plan next friday 9am", with no week start to count from, is nine
        // o'clock on a Friday, and so is "fri 5pm", whose weekday isn't read on its own; today's
        // nine o'clock is a day Todoist would never give either. So the time goes back into the
        // words with the day it belongs to, for the server to read the two together.
        if (time is { } bare && date is null)
        {
            if (declined || WeekdayBeside(tokens, timeStart, timeWords.Length))
            {
                content.InsertRange(timeSlot, timeWords);
                time = null;
            }
            else
            {
                date = DayOfBareTime(bare, now);
            }
        }

        // Resolved now the due time is settled: a "before due" reminder needs one to count back from,
        // and on a task without a time Todoist falls back to the same span from the current time.
        var reminders = new List<CaptureReminder>(pending.Count);
        foreach (var p in pending)
        {
            reminders.Add(p switch
            {
                StagedAbsolute a => new AbsoluteReminder(a.Date, a.Time),
                StagedBeforeDue b when time is not null => new RelativeReminder(b.Minutes),
                StagedBeforeDue b => Absolute(Moment(now, b.Minutes, floorToHour: false)),
                StagedRecurring r => new RecurringReminder(r.Schedule),
                _ => throw new InvalidOperationException(),
            });
        }

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
            Reminders = reminders,
            IsRecurrence = recurrence,
        };

        static AbsoluteReminder Absolute((DateOnly Date, TimeOnly Time) moment) => new(moment.Date, moment.Time);
    }

    /// <summary>
    /// Reads the whole of what was typed into a box asking for a day.
    /// </summary>
    /// <remarks>
    /// Every word has to be accounted for — a day, a time, an <c>at</c> between them — or it wasn't
    /// read. Anything left over is the tell that it wasn't understood: "daily 9am" and "each monday"
    /// both hold a day while dropping the repeat on the floor, and "tomorrow p1" would set a date
    /// and quietly lose the rest.
    ///
    /// A repeat is told by its opening word as well as by <c>every</c>. Not in a capture, where
    /// "Write daily report" is a title, but here the whole input is the schedule, so the first word
    /// can be taken at its face value. Only in English, though: a task told it repeats is advanced
    /// rather than ticked off when it's closed, and for an account that reads another language
    /// whether "every monday" repeats is the server's to say.
    ///
    /// Whatever language the account reads, a box holding a word that isn't read isn't a day, so
    /// figures are as safe to read here as they are in English, where in a capture they aren't:
    /// "jedes Jahr am 25/12" is left whole for the server, never taken as one Christmas Day.
    /// </remarks>
    /// <param name="text">What was typed</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <returns>What it was read as</returns>
    public DayReading ReadDay(string? text, DateSettings settings)
    {
        var tokens = Tokens(text);
        if (tokens.Length == 0)
            return DayReading.Blank;

        if (settings.ReadsEnglish && (RepeatStarters.Contains(tokens[0], StringComparer.OrdinalIgnoreCase) || tokens.Any(IsRepeatWord)))
            return DayReading.Repeat;

        var now = Now(settings);
        DateOnly? date = null;
        TimeOnly? time = null;

        for (var i = 0; i < tokens.Length; i++)
        {
            if (date is null && DayAt(tokens, i, DateOnly.FromDateTime(now), settings, out var used, out _) is { } day)
            {
                date = day;
                i += used - 1;
                continue;
            }

            if (time is null && TimeAt(tokens, i, settings, out used) is { } at)
            {
                time = at;
                i += used - 1;
                continue;
            }

            return DayReading.Unread;
        }

        // Every word was read and there was at least one, so there's a day, a time, or both.
        return DayReading.On(date ?? DayOfBareTime(time!.Value, now), time);
    }

    /// <summary>
    /// A day, and a time on it, written the way this grammar reads them back.
    /// </summary>
    /// <remarks>
    /// What a date box shows for a day picked off its calendar, or for the date a task already has.
    /// The year is always there, since a day without one is read as the next time it comes round,
    /// which for a day already gone is next year's. English whatever the machine's language, since
    /// English is what's read — and in figures for an account that reads another language, where
    /// "4 Aug 2026" isn't read at all.
    /// </remarks>
    /// <param name="day">The day</param>
    /// <param name="time">The time of day on it, or null for the whole day</param>
    /// <param name="english">Whether the account reads a typed date in English</param>
    /// <returns>The words</returns>
    public static string Written(DateOnly day, TimeOnly? time, bool english)
    {
        var written = day.ToString(english ? "d MMM yyyy" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return time is { } at ? $"{written} {at.ToString("HH:mm", CultureInfo.InvariantCulture)}" : written;
    }

    /// <summary>The moment it is now, in the account's timezone.</summary>
    /// <param name="settings">What the account says about reading a date</param>
    /// <returns>The account's wall-clock time</returns>
    private DateTime Now(DateSettings settings) => TimeZoneInfo.ConvertTime(_clock.UtcNow, settings.TimeZone).DateTime;

    /// <summary>Splits text into its words, on any run of whitespace.</summary>
    /// <param name="text">The text</param>
    /// <returns>The words, which is none for blank text</returns>
    private static string[] Tokens(string? text)
        => (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Splits capture text into its words, keeping a place's name whole where its spaces are escaped.
    /// </summary>
    /// <remarks>
    /// Todoist names a project with a space in it by putting a backslash before the space —
    /// <c>#My\ Project</c> — and a section or a label is read the same way here. Only in a word that
    /// opens with <c>#</c>, <c>/</c> or <c>@</c>: anywhere else the backslash is part of the title,
    /// and joining the next word on would stop a day in it being read.
    /// </remarks>
    /// <param name="text">What was typed</param>
    /// <returns>The words, with each escaped place name as one of them, backslashes and all</returns>
    private static string[] CaptureTokens(string text)
    {
        var words = Tokens(text);
        var joined = new List<string>(words.Length);

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];

            if (word.Length > 1 && word[0] is '#' or '/' or '@')
                while (word.EndsWith('\\') && i + 1 < words.Length)
                    word += " " + words[++i];

            joined.Add(word);
        }

        return [.. joined];
    }

    /// <summary>
    /// A project, section or label name written so the grammar reads it back whole.
    /// </summary>
    /// <remarks>
    /// Todoist's escape: a backslash before each space. Without one, <c>#My Project</c> names a
    /// project called "My" and leaves "Project" in the title, here and on the server alike.
    /// </remarks>
    /// <param name="name">The name as the account holds it</param>
    /// <returns>The name ready to follow a <c>#</c>, <c>/</c> or <c>@</c></returns>
    public static string Escaped(string name) => name.Replace(" ", "\\ ");

    /// <summary>A place's name as it was meant, without the backslashes that kept it whole.</summary>
    /// <param name="name">The name as it was typed</param>
    /// <returns>The name as the account would hold it</returns>
    private static string Unescaped(string name) => name.Replace("\\ ", " ");

    /// <summary>Whether a word starts a repeat wherever it comes: <c>every</c>, or Todoist's <c>every!</c>.</summary>
    /// <param name="token">The word</param>
    /// <returns>True when it does</returns>
    private static bool IsRepeatWord(string token)
        => token.Equals("every", StringComparison.OrdinalIgnoreCase) || token.Equals("every!", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The day a time given on its own falls on: today, unless that time has already gone, when
    /// it's tomorrow. Todoist's rule, so "6pm" typed at seven names the same evening either way.
    /// </summary>
    /// <param name="time">The time given</param>
    /// <param name="now">The account's wall-clock time</param>
    /// <returns>The day it falls on</returns>
    private static DateOnly DayOfBareTime(TimeOnly time, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        return time < TimeOnly.FromDateTime(now) ? today.AddDays(1) : today;
    }

    /// <summary>
    /// Whether a shortened weekday sits right beside a time, as "fri" does in "fri 5pm".
    /// </summary>
    /// <remarks>
    /// A shortened weekday isn't read on its own, since "sat" and "sun" are words as often as days.
    /// Beside a time it's a day as plainly as anything is, and the time is that day's, not today's.
    /// </remarks>
    /// <param name="tokens">Every word typed</param>
    /// <param name="start">Where the time's words start</param>
    /// <param name="count">How many words the time took</param>
    /// <returns>True when the word either side of the time is a shortened weekday</returns>
    private static bool WeekdayBeside(string[] tokens, int start, int count)
    {
        bool Shortened(int index)
            => index >= 0 && index < tokens.Length && WeekdaysShort.ContainsKey(tokens[index].ToLowerInvariant());

        return Shortened(start - 1) || Shortened(start + count);
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

    /// <summary>
    /// Reads a day starting at one word, which may run on over the next few.
    /// </summary>
    /// <param name="tokens">Every word typed</param>
    /// <param name="at">Where the day would start</param>
    /// <param name="today">Today in the account's timezone</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <param name="used">How many words the day took, or would have taken when it was passed over</param>
    /// <param name="declined">
    /// Whether a day starts there that Todoist would read and this won't — one that turns on a
    /// setting the account hasn't given, a year in two figures, or a weekend, which this doesn't
    /// read at all
    /// </param>
    /// <returns>The day, or null when no day starts there that can be read here</returns>
    private static DateOnly? DayAt(string[] tokens, int at, DateOnly today, DateSettings settings, out int used, out bool declined)
    {
        string? Word(int offset) => at + offset < tokens.Length ? tokens[at + offset].ToLowerInvariant() : null;

        var first = Word(0)!;
        used = 1;

        if (Figures(first, today, settings, out declined) is { } figures)
            return figures;

        // Everything after figures is English words, which an account that reads another language
        // reads as its own words or not at all.
        if (declined || !settings.ReadsEnglish)
            return null;

        switch (first)
        {
            case "today" or "tod":
                return today;
            case "tomorrow" or "tom":
                return today.AddDays(1);
        }

        if (Array.IndexOf(WeekdayNames, first) is var named and >= 0)
            return Coming((DayOfWeek)named, today);

        used = 2;

        if (first == "this" && Weekday(Word(1)) is { } thisOne)
            return Coming(thisOne, today);

        if (first == "next" && NextOne(Word(1), today, settings) is { } nextOne)
            return nextOne;

        if ((first == "next" && (Weekday(Word(1)) is not null || Word(1) is "week" or "weekend"))
            || (first == "this" && Word(1) == "weekend"))
        {
            declined = true;
            return null;
        }

        used = 3;

        if (first == "in" && Count(Word(1)) is { } count && Later(today, count, Word(2)) is { } later)
            return later;

        if (first == "end" && Word(1) == "of" && Word(2) == "month")
            return new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

        return Named(Word(0)!, Word(1), Word(2), today, out used, out declined);
    }

    /// <summary>A weekday by its name, in full or shortened.</summary>
    /// <param name="word">The word, lower-cased, or null when there isn't one</param>
    /// <returns>The day, or null when the word doesn't name one</returns>
    internal static DayOfWeek? Weekday(string? word)
    {
        if (word is null)
            return null;

        if (Array.IndexOf(WeekdayNames, word) is var named and >= 0)
            return (DayOfWeek)named;

        return WeekdaysShort.TryGetValue(word, out var shortened) ? shortened : null;
    }

    /// <summary>The coming occurrence of a weekday, counting today when it already matches.</summary>
    /// <param name="day">The weekday</param>
    /// <param name="today">Today</param>
    /// <returns>The day</returns>
    private static DateOnly Coming(DayOfWeek day, DateOnly today)
        => today.AddDays(((int)day - (int)today.DayOfWeek + 7) % 7);

    /// <summary>
    /// What <c>next</c> and the word after it name.
    /// </summary>
    /// <remarks>
    /// "next friday" is the Friday of the week after this one — not the coming Friday, which is what
    /// "friday" is. Todoist puts it as skipping this week's when it hasn't happened yet, and taking
    /// the next one when it has; either way that's the following week's, which is how it's worked
    /// out here. Where a week starts is the account's setting, so without it this isn't read. "next
    /// week" is the account's own setting outright, and neither is it read without one.
    /// </remarks>
    /// <param name="word">The word after <c>next</c>, lower-cased, or null when there isn't one</param>
    /// <param name="today">Today</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <returns>The day, or null when the words don't name one that can be worked out here</returns>
    private static DateOnly? NextOne(string? word, DateOnly today, DateSettings settings)
    {
        switch (word)
        {
            case "week":
                return FilterDay.NextWeek().Resolve(today, settings.NextWeek);
            case "month":
                return MonthsOn(today, 1);
            case "year":
                return today.Year < DateOnly.MaxValue.Year ? new DateOnly(today.Year + 1, 1, 1) : null;
        }

        if (Weekday(word) is not { } day || settings.WeekStart is not { } start)
            return null;

        var weekBegan = today.AddDays(-(((int)today.DayOfWeek - (int)start + 7) % 7));
        return weekBegan.AddDays(7 + (((int)day - (int)start + 7) % 7));
    }

    /// <summary>A count written in figures, as in "in 3 days".</summary>
    /// <param name="word">The word, or null when there isn't one</param>
    /// <returns>The count, or null when the word isn't one</returns>
    private static int? Count(string? word)
        => word is { Length: >= 1 and <= 4 } && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;

    /// <summary>The day some days, weeks or months on from today.</summary>
    /// <param name="today">Today</param>
    /// <param name="count">How many</param>
    /// <param name="unit">Of what, lower-cased, or null when the words stopped short</param>
    /// <returns>The day, or null when the unit isn't one or the day is past the calendar's end</returns>
    private static DateOnly? Later(DateOnly today, int count, string? unit) => unit switch
    {
        "day" or "days" => DaysOn(today, count),
        "week" or "weeks" => DaysOn(today, count * 7),
        "month" or "months" => MonthsOn(today, count),
        _ => null,
    };

    /// <summary>Today and some days, held to the end of the calendar.</summary>
    private static DateOnly? DaysOn(DateOnly today, int days)
        => today.DayNumber + (long)days <= DateOnly.MaxValue.DayNumber ? today.AddDays(days) : null;

    /// <summary>Today and some months, held to the end of the calendar.</summary>
    private static DateOnly? MonthsOn(DateOnly today, int months)
        => (DateOnly.MaxValue.Year - today.Year) * 12 + (12 - today.Month) >= months ? today.AddMonths(months) : null;

    /// <summary>
    /// A day written in figures: <c>2026-12-25</c>, <c>2026/12/25</c>, or the day and month in the
    /// account's order with an optional year — <c>25/12</c>, <c>25/12/2026</c>.
    /// </summary>
    /// <param name="word">The word</param>
    /// <param name="today">Today, which a day without a year is counted on from</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <param name="undecided">Whether the word is a date either way round, and the account hasn't said which</param>
    /// <returns>The day, or null when the word isn't one that can be read here</returns>
    private static DateOnly? Figures(string word, DateOnly today, DateSettings settings, out bool undecided)
    {
        undecided = false;

        if (DateOnly.TryParseExact(word, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso;

        if (YearFirstPattern().Match(word) is { Success: true } yearFirst)
            return Exactly(Number(yearFirst, "y"), Number(yearFirst, "m"), Number(yearFirst, "d"));

        if (DayAndMonthPattern().Match(word) is not { Success: true } figures)
            return null;

        var (first, second) = (Number(figures, "a"), Number(figures, "b"));

        if (Ordered(first, second, settings.DayFirst) is not { } order)
        {
            // Only an order the account hasn't given stands between it and a day: told one, it
            // would have been read.
            undecided = Ordered(first, second, dayFirst: true) is not null;
            return null;
        }

        var (day, month) = order;
        return figures.Groups["y"].Success
            ? Exactly(Number(figures, "y"), month, day)
            : Next(month, day, today);

        static int Number(Match match, string group)
            => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Which of the two numbers in a date written in figures is the day and which the month.
    /// </summary>
    /// <remarks>
    /// Lenient, as Todoist is: when only one way round can be a date — "25/12", where 25 can't be a
    /// month — that's the one, whichever order the account writes in. Only when both can be does
    /// the account's order decide, and an account that hasn't said leaves "5/6" to the server
    /// rather than have it mean one day here and another there.
    /// </remarks>
    /// <param name="first">The number written first</param>
    /// <param name="second">The number written second</param>
    /// <param name="dayFirst">Whether the account writes the day first, or null when it hasn't said</param>
    /// <returns>The day and the month, or null when it can't be told which is which</returns>
    private static (int Day, int Month)? Ordered(int first, int second, bool? dayFirst)
    {
        var asDayFirst = first is >= 1 and <= 31 && second is >= 1 and <= 12;
        var asMonthFirst = first is >= 1 and <= 12 && second is >= 1 and <= 31;

        if (asDayFirst && asMonthFirst && first != second)
        {
            return dayFirst switch
            {
                true => (first, second),
                false => (second, first),
                null => null,
            };
        }

        if (asDayFirst)
            return (first, second);

        return asMonthFirst ? (second, first) : null;
    }

    /// <summary>
    /// A day and a month by name, either way round, with an optional year after them: <c>4 aug</c>,
    /// <c>aug 4th</c>, <c>4 August 2027</c>, <c>Aug 4, 2027</c>.
    /// </summary>
    /// <remarks>
    /// A comma after the day is only taken when a year follows it, as in a date written out in
    /// full. Anywhere else it's the sentence's punctuation, and the day isn't read.
    ///
    /// A year in two figures isn't read here, and Todoist may well read one, so a day with one
    /// after it is passed over whole. Read without it, "4 Aug 27" was this year's 4 August with
    /// "27" left in the words.
    /// </remarks>
    /// <param name="first">The first word, lower-cased</param>
    /// <param name="second">The word after it, or null</param>
    /// <param name="third">The word after that, or null</param>
    /// <param name="today">Today, which a day without a year is counted on from</param>
    /// <param name="used">How many words the day took, or would have taken when it was passed over</param>
    /// <param name="declined">Whether it was passed over for a year written in two figures</param>
    /// <returns>The day, or null when the words don't name one that can be read here</returns>
    private static DateOnly? Named(string first, string? second, string? third, DateOnly today, out int used, out bool declined)
    {
        used = 0;
        declined = false;
        int day;
        int month;

        if (DayNumber(first) is { } dayFirst && second is not null && Months.TryGetValue(second, out var named))
        {
            (day, month) = (dayFirst, named);
        }
        else if (Months.TryGetValue(first, out named) && second is not null)
        {
            var withComma = second.EndsWith(',');
            if (DayNumber(withComma ? second[..^1] : second) is not { } after)
                return null;

            if (withComma && Year(third) is null && !ShortYear(third))
                return null;

            (day, month) = (after, named);
        }
        else
        {
            return null;
        }

        if (ShortYear(third))
        {
            used = 3;
            declined = true;
            return null;
        }

        if (Year(third) is { } year)
        {
            used = 3;
            return Exactly(year, month, day);
        }

        used = 2;
        return Next(month, day, today);
    }

    /// <summary>A day of the month, with or without its ordinal: <c>4</c>, <c>4th</c>.</summary>
    /// <param name="word">The word, lower-cased</param>
    /// <returns>The day, or null when the word isn't one</returns>
    private static int? DayNumber(string word)
        => DayNumberPattern().Match(word) is { Success: true } match
           && int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture) is var day and >= 1 and <= 31
            ? day
            : null;

    /// <summary>Whether a word is a year in two figures, which this doesn't read: <c>27</c>.</summary>
    /// <param name="word">The word, or null when there isn't one</param>
    /// <returns>True when it's two figures and nothing else</returns>
    private static bool ShortYear(string? word) => word is { Length: 2 } && char.IsAsciiDigit(word[0]) && char.IsAsciiDigit(word[1]);

    /// <summary>A year in four figures.</summary>
    /// <param name="word">The word, or null when there isn't one</param>
    /// <returns>The year, or null when the word isn't one</returns>
    private static int? Year(string? word)
        => word is { Length: 4 } && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year >= 1
            ? year
            : null;

    /// <summary>A day in a given year, when there is one.</summary>
    /// <returns>The day, or null when that month hasn't got it — 31 April, or 29 February in 2027</returns>
    private static DateOnly? Exactly(int year, int month, int day)
        => year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;

    /// <summary>
    /// The next time a day of the year comes round, counting today: this year's when it's still to
    /// come, and a later year's when it has gone.
    /// </summary>
    /// <remarks>
    /// Looks a few years ahead rather than one, since 29 February can be up to eight years off.
    /// </remarks>
    /// <returns>The day, or null when no year has it — 31 April</returns>
    private static DateOnly? Next(int month, int day, DateOnly today)
    {
        for (var year = today.Year; year <= Math.Min(today.Year + 8, DateOnly.MaxValue.Year); year++)
        {
            if (Exactly(year, month, day) is { } candidate && candidate >= today)
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Reads a time of day starting at one word, taking an <c>at</c> in front of it along with it.
    /// </summary>
    /// <param name="tokens">Every word typed</param>
    /// <param name="at">Where the time would start</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <param name="used">How many words the time took, when there was one</param>
    /// <returns>The time, or null when no time starts there</returns>
    private static TimeOnly? TimeAt(string[] tokens, int at, DateSettings settings, out int used)
    {
        var english = settings.ReadsEnglish;

        used = 2;
        if (english
            && tokens[at].Equals("at", StringComparison.OrdinalIgnoreCase)
            && at + 1 < tokens.Length
            && TryParseTime(tokens[at + 1], english, out var after))
        {
            return after;
        }

        used = 1;
        return TryParseTime(tokens[at], english, out var time) ? time : null;
    }

    /// <summary>A time of day in one word: <c>16:30</c>, and <c>4pm</c> or <c>4:30pm</c> in English.</summary>
    /// <param name="token">The word</param>
    /// <param name="english">Whether the account reads a typed date in English, without which "am" and "pm" aren't read</param>
    /// <param name="time">The time, when the word is one</param>
    /// <returns>True when the word is a time</returns>
    internal static bool TryParseTime(string token, bool english, out TimeOnly time)
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
            if (!english || hour is < 1 or > 12)
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

    // ---- Reminders ---------------------------------------------------------------------------------

    /// <summary>When a reminder set for a day with no time of its own fires. Todoist's default.</summary>
    public static readonly TimeOnly ReminderDefaultTime = new(9, 0);

    /// <summary>A reminder staged while a capture is read, before the due time is settled.</summary>
    private abstract record PendingReminder;

    /// <summary>A reminder for a moment of its own, already worked out.</summary>
    private sealed record StagedAbsolute(DateOnly Date, TimeOnly Time) : PendingReminder;

    /// <summary>
    /// A reminder a span before the task is due. It becomes a relative reminder when the task has a
    /// due time to count back from, or that same span from now when it hasn't, Todoist's own fallback.
    /// </summary>
    private sealed record StagedBeforeDue(int Minutes) : PendingReminder;

    /// <summary>A reminder that repeats, kept as the words the server reads it from.</summary>
    private sealed record StagedRecurring(string Schedule) : PendingReminder;

    /// <summary>The parts of the day a recurring reminder can name alone, each with a time Todoist gives it.</summary>
    private static readonly string[] PartsOfDay = ["morning", "afternoon", "evening", "night"];

    /// <summary>What a recurring reminder can repeat on with nothing before it, ahead of its time.</summary>
    private static readonly string[] RepeatPeriods = ["day", "week", "month", "weekday", "workday", "weekend"];

    /// <summary>The periods a count or an <c>other</c> can go in front of.</summary>
    private static readonly string[] CountedPeriods = ["day", "week", "month"];

    /// <summary>
    /// Reads the reminder a <c>!</c> shortcut names, starting at one word and running on over the
    /// next few where it must (<c>!1h before</c>, <c>!tmr 3pm</c>).
    /// </summary>
    /// <remarks>
    /// Todoist's documented shortcuts: a span from now (<c>!30m</c>, <c>!2h30m</c>, <c>!later</c>), a
    /// span before the task is due (<c>!30mb</c>, <c>!1h before</c>), or a day and time of its own
    /// (<c>!3pm</c>, <c>!tmr 3pm</c>, <c>!Mon 9am</c>, <c>!tomorrow</c> at nine). A day with no time
    /// fires at <see cref="ReminderDefaultTime"/>, and a time with no day on its next instance, both
    /// as Todoist does. "tmr" and a bare shortened weekday are read as days here and nowhere else,
    /// since after a <c>!</c> they can't be anything but a day.
    /// </remarks>
    /// <param name="tokens">Every word typed</param>
    /// <param name="at">Where the <c>!</c> word is</param>
    /// <param name="now">The account's wall-clock time</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <param name="used">How many words the reminder took, when there was one</param>
    /// <returns>The reminder, or null when the words after the <c>!</c> aren't one this can read</returns>
    private PendingReminder? ReminderAt(string[] tokens, int at, DateTime now, DateSettings settings, out int used)
    {
        used = 1;
        var spec = tokens[at][1..].ToLowerInvariant();

        // "around 4 hours later from current time, rounded down to the closest hour".
        if (spec == "later")
        {
            var (d, t) = Moment(now, 4 * 60, floorToHour: true);
            return new StagedAbsolute(d, t);
        }

        if (SpanMinutes(spec) is { } span)
        {
            // "!1h before" / "!30m before": a second word spells out the "b" of "!1hb".
            if (at + 1 < tokens.Length && tokens[at + 1].Equals("before", StringComparison.OrdinalIgnoreCase))
            {
                used = 2;
                return new StagedBeforeDue(span);
            }

            var (d, t) = Moment(now, span, floorToHour: false);
            return new StagedAbsolute(d, t);
        }

        if (BeforeMinutes(spec) is { } before)
            return new StagedBeforeDue(before);

        return AbsoluteReminderAt(tokens, at, now, settings, out used);
    }

    /// <summary>Reads a reminder written as a day and/or a time of its own, after the <c>!</c>.</summary>
    /// <param name="tokens">Every word typed</param>
    /// <param name="at">Where the <c>!</c> word is</param>
    /// <param name="now">The account's wall-clock time</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <param name="used">How many words it took, when it read one</param>
    /// <returns>The reminder, or null when no day or time starts after the <c>!</c></returns>
    private PendingReminder? AbsoluteReminderAt(string[] tokens, int at, DateTime now, DateSettings settings, out int used)
    {
        used = 1;
        var today = DateOnly.FromDateTime(now);

        // The reminder reads over its own words: the first is the "!" word with the mark taken off.
        var syn = new string[tokens.Length - at];
        syn[0] = tokens[at][1..];
        Array.Copy(tokens, at + 1, syn, 1, syn.Length - 1);

        DateOnly? day = null;
        var dayWords = 0;
        if (DayAt(syn, 0, today, settings, out var dUsed, out _) is { } readDay)
        {
            day = readDay;
            dayWords = dUsed;
        }
        else if (syn[0].Equals("tmr", StringComparison.OrdinalIgnoreCase))
        {
            day = today.AddDays(1);
            dayWords = 1;
        }
        else if (settings.ReadsEnglish && Weekday(syn[0].ToLowerInvariant()) is { } weekday)
        {
            day = Coming(weekday, today);
            dayWords = 1;
        }

        TimeOnly? clock = null;
        var timeWords = 0;
        if (dayWords < syn.Length && TimeAt(syn, dayWords, settings, out var tUsed) is { } readTime)
        {
            clock = readTime;
            timeWords = tUsed;
        }

        if (day is { } onDay && clock is { } atTime)
        {
            used = dayWords + timeWords;
            return new StagedAbsolute(onDay, atTime);
        }

        if (day is { } dayOnly)
        {
            used = dayWords;
            return new StagedAbsolute(dayOnly, ReminderDefaultTime);
        }

        if (clock is { } timeOnly)
        {
            used = timeWords;
            return new StagedAbsolute(DayOfBareTime(timeOnly, now), timeOnly);
        }

        used = 1;
        return null;
    }

    /// <summary>
    /// Reads how far a recurring reminder's words run, from the <c>!every</c> that opens it.
    /// </summary>
    /// <remarks>
    /// Only a schedule Todoist is known to take. One that repeats on a day, a weekday, a week or a
    /// month, counted (<c>2 weeks</c>) or every other one, at a time (<c>!every sat 9am</c>,
    /// <c>!every other day at 17:30</c>, <c>!every 5pm</c>). One that repeats every so many hours
    /// (<c>!every 2 hours</c>). Or a part of the day, which brings its own time
    /// (<c>!every evening</c>). Every word comes from a closed set, so the run stops at the first one
    /// that isn't and the task's words carry on from there. Todoist refuses a schedule with a title
    /// word left on the end, and one that names no time, so anything else is left for the caller to
    /// flag rather than sent off to fail.
    /// </remarks>
    /// <param name="tokens">Every word typed</param>
    /// <param name="at">Where the <c>!every</c> word is</param>
    /// <param name="settings">What the account says about reading a date</param>
    /// <param name="used">How many words the reminder took, when there was one</param>
    /// <returns>The schedule as typed, without its <c>!</c>, or null when it isn't one this can read</returns>
    private static string? RecurringReminderAt(string[] tokens, int at, DateSettings settings, out int used)
    {
        used = 1;
        if (End() is not { } end)
            return null;

        used = end - at;
        return string.Join(' ', tokens[at..end])[1..];

        int? End()
        {
            var next = at + 1;
            var word = Word(next);

            // A part of the day brings its own time, and one that repeats hourly needs none.
            if ((word is not null && PartsOfDay.Contains(word)) || word == "hour")
                return next + 1;

            if (Interval(word) is { } count)
            {
                var unit = Word(next + 1);
                var singular = count == 1;
                if (unit == (singular ? "hour" : "hours"))
                    return next + 2;
                if (unit is null || !CountedPeriods.Any(p => unit == (singular ? p : p + "s")))
                    return null;
                next += 2;
            }
            else if (word == "other")
            {
                var period = Word(next + 1);
                if (period is null || (!CountedPeriods.Contains(period) && Weekday(period) is null))
                    return null;
                next += 2;
            }
            else if (word is not null && (RepeatPeriods.Contains(word) || Weekday(word) is not null))
            {
                next += 1;
            }
            else if (word == "at")
            {
                // An "at" goes after what it repeats on. Todoist has never been seen to take
                // "every at 9am", so it's flagged rather than sent off to fail.
                return null;
            }

            // Anything slower than hourly fires at a time, and Todoist won't take one without it.
            return next < tokens.Length && TimeAt(tokens, next, settings, out var took) is not null ? next + took : null;
        }

        string? Word(int i) => i < tokens.Length ? tokens[i].ToLowerInvariant() : null;
    }

    /// <summary>A count in front of a period, as in <c>every 2 weeks</c>: one or two figures, with no nought in front.</summary>
    /// <param name="word">The word, or null when there isn't one</param>
    /// <returns>The count, or null when the word isn't one</returns>
    private static int? Interval(string? word)
        => word is { Length: 1 or 2 } && word[0] != '0' && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;

    /// <summary>A span written as hours and/or minutes: <c>30m</c>, <c>90min</c>, <c>2h</c>, <c>2h30m</c>.</summary>
    /// <param name="word">The word, lower-cased</param>
    /// <returns>The span in minutes, or null when the word isn't one</returns>
    private static int? SpanMinutes(string word)
    {
        var match = SpanPattern().Match(word);
        if (!match.Success || (!match.Groups["h"].Success && !match.Groups["m"].Success))
            return null;

        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        return hours * 60 + minutes;
    }

    /// <summary>A span before the task is due, written in one word: <c>30mb</c>, <c>1hb</c>, <c>0mb</c>.</summary>
    /// <param name="word">The word, lower-cased</param>
    /// <returns>The span in minutes, or null when the word isn't one</returns>
    private static int? BeforeMinutes(string word)
    {
        var match = BeforePattern().Match(word);
        if (!match.Success)
            return null;

        var count = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        return match.Groups["u"].Value == "h" ? count * 60 : count;
    }

    /// <summary>A moment a span of minutes on from now, to the minute, or floored to the hour.</summary>
    /// <param name="now">The account's wall-clock time</param>
    /// <param name="minutes">The span</param>
    /// <param name="floorToHour">Whether to drop the minutes, as <c>!later</c> does</param>
    /// <returns>The day and time it lands on</returns>
    private static (DateOnly Date, TimeOnly Time) Moment(DateTime now, int minutes, bool floorToHour)
    {
        var at = now.AddMinutes(minutes);
        return (DateOnly.FromDateTime(at), floorToHour ? new TimeOnly(at.Hour, 0) : new TimeOnly(at.Hour, at.Minute));
    }

    // [0-9] and never \d, which takes in every script's digits: int.Parse throws on a full-width
    // one, and the date box reads on every keystroke.
    [GeneratedRegex(@"^(?<h>[0-9]{1,2})(:(?<m>[0-9]{2}))?(?<ap>am|pm|AM|PM)?$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^(?<y>[0-9]{4})/(?<m>[0-9]{1,2})/(?<d>[0-9]{1,2})$")]
    private static partial Regex YearFirstPattern();

    [GeneratedRegex(@"^(?<a>[0-9]{1,2})/(?<b>[0-9]{1,2})(/(?<y>[0-9]{4}))?$")]
    private static partial Regex DayAndMonthPattern();

    [GeneratedRegex(@"^(?<d>[0-9]{1,2})(st|nd|rd|th)?$")]
    private static partial Regex DayNumberPattern();

    [GeneratedRegex(@"^(?:(?<h>[0-9]{1,4})h)?(?:(?<m>[0-9]{1,4})m(?:in)?)?$")]
    private static partial Regex SpanPattern();

    [GeneratedRegex(@"^(?<n>[0-9]{1,4})(?<u>m|h)b$")]
    private static partial Regex BeforePattern();
}
