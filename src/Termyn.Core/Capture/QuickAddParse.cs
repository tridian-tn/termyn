using Termyn.Core.Model;

namespace Termyn.Core.Capture;

/// <summary>The result of parsing quick-add text locally.</summary>
public sealed record QuickAddParse
{
    /// <summary>The task text with every recognised token removed.</summary>
    public required string Content { get; init; }

    public string? ProjectName { get; init; }
    public string? SectionName { get; init; }

    /// <summary>
    /// Every word of the title as typed, the <c>#project</c> and <c>/section</c> tokens included,
    /// which <see cref="TitleKeeping"/> rebuilds the title from. Empty for a parse made by hand.
    /// </summary>
    internal IReadOnlyList<string> Words { get; init; } = [];

    /// <summary>Where in <see cref="Words"/> the project's token is, or -1 when none was read.</summary>
    internal int ProjectAt { get; init; } = -1;

    /// <summary>Where in <see cref="Words"/> the section's token is, or -1 when none was read.</summary>
    internal int SectionAt { get; init; } = -1;

    /// <summary>
    /// The title with the place the text named left in it, where it was typed.
    /// </summary>
    /// <remarks>
    /// For a caller that can't put the task where the text says: a name no project has, one two
    /// projects share, or a sub-task, which lives where its parent does. Todoist keeps an unknown
    /// <c>#word</c> in the title, and "Fix issue #42" is a title rather than a place.
    /// </remarks>
    /// <param name="project">Whether to leave the project's token in the title</param>
    /// <param name="section">Whether to leave the section's token in the title</param>
    /// <returns>The title</returns>
    public string TitleKeeping(bool project, bool section)
    {
        project &= ProjectName is not null;
        section &= SectionName is not null;

        if (!project && !section)
            return Content;

        // Made by hand rather than by the parser, so there's no telling where the token was. On
        // the end, which is a word out of order rather than a word missing.
        if (Words.Count == 0)
        {
            var words = new List<string>();
            if (Content.Length > 0)
                words.Add(Content);
            if (project)
                words.Add("#" + QuickAddParser.Escaped(ProjectName!));
            if (section)
                words.Add("/" + QuickAddParser.Escaped(SectionName!));
            return string.Join(' ', words);
        }

        return string.Join(' ', Words.Where((_, at) => (at != ProjectAt || project) && (at != SectionAt || section)));
    }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public Priority Priority { get; init; } = Priority.P4;
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }

    /// <summary>
    /// Tokens the offline parser recognised but cannot act on — recurrence, assignees, and a
    /// reminder it couldn't read (a recurring <c>!every</c>, or a form it hasn't the grammar for).
    /// The capture UI surfaces these so the user knows what was left out. A reminder it could read
    /// is in <see cref="Reminders"/> instead, not here.
    /// </summary>
    public IReadOnlyList<string> Unsupported { get; init; } = [];

    /// <summary>
    /// The reminders the text's <c>!</c> shortcuts name, read the way Todoist's apps read them.
    /// Empty when none were typed, or when a <c>!</c> was flagged in <see cref="Unsupported"/>
    /// rather than read. The caller adds these onto the task once it has an id, and only when the
    /// plan takes reminders.
    /// </summary>
    public IReadOnlyList<CaptureReminder> Reminders { get; init; } = [];

    /// <summary>
    /// Whether the text describes a repeating schedule. Only the server resolves one, and a caller
    /// must not act on <see cref="DueDate"/> when this is set: "every day at 9am" is a schedule,
    /// and the nine o'clock in it is not a due time of its own.
    /// </summary>
    public bool IsRecurrence { get; init; }
}
