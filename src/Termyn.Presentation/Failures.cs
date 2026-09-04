using System.Text.Json.Nodes;
using Termyn.Core.Model;
using Termyn.Core.Sync;

namespace Termyn.Presentation;

/// <summary>
/// A write the server refused, in the words of whoever made it.
/// </summary>
/// <param name="Uuid">Which command it was, for dismissing it</param>
/// <param name="Change">What was being done, as a person would say it</param>
/// <param name="Subject">What it was being done to, or null where nothing names it any more</param>
/// <param name="Reason">What the server said, or null where it said nothing</param>
/// <param name="DiscardsWork">
/// Whether letting it go takes something off this machine. A change to something that already
/// exists was put back when the engine gave up on it, so dismissing costs nothing.
///
/// A creation is the other way about. One the server refuses outright is cancelled there and then
/// and never reaches this list at all, so a creation that does reach it is one the server never
/// ruled on — which is why what was typed was kept, and dismissing is what finally takes it away.
/// </param>
/// <param name="Unruled">
/// Whether the server never said either way, as against having refused it. Only a refusal says
/// where the change ended up: silence leaves it unknown, and the account may have it after all.
/// </param>
public sealed record FailedChange(
    string Uuid,
    string Change,
    string? Subject,
    string? Reason,
    bool DiscardsWork,
    bool Unruled)
{
    /// <summary>
    /// The one line of it: what was being done, and to what.
    /// </summary>
    /// <remarks>
    /// Here with the rest of the wording rather than in the window, so all of what the user reads
    /// about a change that didn't happen is written and tested in one place. A list of these shows
    /// them by this.
    /// </remarks>
    public override string ToString() => Subject is { } subject ? $"{Change} — {subject}" : Change;
}

/// <summary>
/// Reads the outbox's failures into something a window can show.
/// </summary>
/// <remarks>
/// A command the server keeps refusing stops being retried and stays in the outbox, which is what
/// keeps the count in the status bar honest. Until there was something like this it was also all
/// the user got: a permanent "1 failed" naming nothing, explaining nothing, and with no way to be
/// rid of it.
///
/// Framework-free and separate from the presenter so the wording has tests of its own — it is the
/// whole of what the user gets to read about a change that didn't happen.
/// </remarks>
public static class Failures
{
    /// <summary>Everything in the outbox the server has finished refusing.</summary>
    /// <param name="outbox">The queue as it stands</param>
    /// <param name="snapshot">The model, for naming what each command was about</param>
    /// <returns>One entry per failure, oldest first</returns>
    public static IReadOnlyList<FailedChange> From(IEnumerable<OutboxCommand> outbox, ModelSnapshot snapshot)
        => outbox
            .Where(c => c.State == OutboxState.Failed)
            .OrderBy(c => c.Seq)
            .Select(c => Describe(c, snapshot))
            .ToList();

    private static FailedChange Describe(OutboxCommand command, ModelSnapshot snapshot)
        => new(
            command.Uuid,
            Change(command.Type),
            Subject(command, snapshot),
            Reason(command),
            Creates(command),
            Unruled(command));

    /// <summary>
    /// Whether the server never said either way, as against having refused it.
    /// </summary>
    /// <remarks>
    /// The two are worth telling apart, because only one of them says where the change ended up. A
    /// refusal is a definite answer: it didn't happen, and the account doesn't have it. Silence is
    /// not — the engine gives up after so many rounds of it, and the account may well have the
    /// change anyway. Counted rather than read off the message, which is ours and may be reworded.
    /// </remarks>
    private static bool Unruled(OutboxCommand command) => command.NoVerdictRounds > 0;

    /// <summary>Whether this command was making something that had not existed before.</summary>
    /// <remarks>
    /// The same test the engine makes, and it has to stay the same one: it decides both whether the
    /// local copy was kept when the command failed and whether dismissing it takes that copy away.
    /// </remarks>
    private static bool Creates(OutboxCommand command) => command.Type.EndsWith("_add", StringComparison.Ordinal);

    /// <summary>What a command was doing, written the way someone would say it.</summary>
    /// <remarks>
    /// Every type the engine queues has a line here. One it doesn't is shown as itself rather than
    /// as a guess — a name nobody recognises is still better than a confident description of the
    /// wrong thing, and it says plainly that this list needs a line adding to it.
    /// </remarks>
    private static string Change(string type) => type switch
    {
        "item_add" => "Adding a task",
        "item_update" => "Changing a task",
        "item_delete" => "Deleting a task",
        "item_close" => "Completing a task",
        "item_uncomplete" => "Reopening a task",
        "item_move" => "Moving a task",
        "item_reorder" => "Reordering tasks",
        "project_add" => "Adding a project",
        "section_add" => "Adding a section",
        "label_add" => "Adding a label",
        "label_delete" => "Deleting a label",
        "note_add" => "Adding a comment",
        "reminder_add" => "Adding a reminder",
        _ => type,
    };

    /// <summary>What the server said about it.</summary>
    /// <remarks>
    /// Passed through as it came. It is the server's own words rather than ours, and rewording it
    /// would only put a second guess between the user and the reason.
    /// </remarks>
    private static string? Reason(OutboxCommand command)
        => string.IsNullOrWhiteSpace(command.LastError) ? null : command.LastError.Trim();

    /// <summary>
    /// What the command was about, named from the model rather than from the command.
    /// </summary>
    /// <remarks>
    /// A command carries only the fields it changed, so its own arguments rarely say what it was
    /// about — an <c>item_update</c> setting a due date names an id and a date and nothing a person
    /// would recognise. A refused creation is looked up under the temporary id it was given, since
    /// the server never got far enough to hand back a real one.
    ///
    /// Null where nothing answers to the id. A deletion that failed after the resource had gone,
    /// or one naming something this machine never had, is better shown with no name at all than
    /// with a wrong one.
    /// </remarks>
    private static string? Subject(OutboxCommand command, ModelSnapshot snapshot)
    {
        var family = command.Type.Split('_')[0];

        // A comment or a reminder is about something else, and it is that something the user would
        // recognise. Its own id names a note nobody has ever seen on screen.
        if (family is "note" or "reminder")
        {
            var args = Arguments(command);
            return Named(snapshot, "item", Text(args, "item_id"))
                ?? Named(snapshot, "project", Text(args, "project_id"));
        }

        return Named(snapshot, family, Identifier(command));
    }

    /// <summary>What a resource of the given family is called, or null where nothing answers to it.</summary>
    private static string? Named(ModelSnapshot snapshot, string family, string? id)
    {
        if (id is not { Length: > 0 })
            return null;

        var named = family switch
        {
            "project" => snapshot.Projects.FirstOrDefault(p => p.Id == id)?.Name,
            "section" => snapshot.Sections.FirstOrDefault(s => s.Id == id)?.Name,
            "label" => snapshot.Labels.FirstOrDefault(l => l.Id == id)?.Name,
            _ => snapshot.Items.FirstOrDefault(i => i.Id == id)?.Content,
        };

        return string.IsNullOrWhiteSpace(named) ? null : named;
    }

    private static string? Text(JsonObject? args, string key)
        => args?[key] is JsonValue value ? value.ToString() : null;

    /// <summary>The id the command names, which for a creation is the temporary one it was given.</summary>
    private static string? Identifier(OutboxCommand command)
    {
        if (command.TempId is { Length: > 0 } temp)
            return temp;

        return Text(Arguments(command), "id");
    }

    /// <summary>
    /// A command's arguments, or null where they can't be read.
    /// </summary>
    /// <remarks>
    /// Read rather than trusted. They are stored as text, so a row written by an older build is
    /// reachable here — and the window this feeds is the one somebody opened to clear a failure,
    /// which is a poor moment for it to throw.
    /// </remarks>
    private static JsonObject? Arguments(OutboxCommand command)
    {
        try
        {
            return JsonNode.Parse(command.ArgsJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
