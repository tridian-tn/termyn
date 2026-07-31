using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Filters;
using Termyn.Core.Model;
using Termyn.Core.Platform;
using Termyn.Core.Sync;

namespace Termyn.Presentation;

/// <summary>A single row rendered in the outline. <paramref name="Depth"/> is its indent level.</summary>
public sealed record TaskRow(
    string Id,
    string Content,
    Priority Priority,
    string Project,
    string Due,
    IReadOnlyList<string> Labels,
    int Depth = 0,
    bool IsRecurring = false,
    int ReminderCount = 0,
    bool Completed = false);

/// <summary>
/// What the local parser made of some capture text, and how its names resolved.
/// <paramref name="Parse"/> already carries the text the task would be created with.
/// </summary>
public sealed record CapturePreview(QuickAddParse Parse, bool ProjectResolved, bool SectionResolved);

/// <summary>
/// Drives the sidebar and the outline: exposes the engine's model as rows and turns user intents
/// into engine operations. UI-framework agnostic, so another platform's view can bind to the same
/// presenter.
/// </summary>
public sealed class MainPresenter
{
    private readonly SyncEngine _engine;
    private readonly QuickAddParser _parser;
    private readonly IClock _clock;

    /// <summary>
    /// Serialises publishing. Intents run on the UI thread while the background sync publishes from
    /// its worker; without this an older snapshot can be assigned after a newer one and put a
    /// completed task back on screen.
    /// </summary>
    private readonly Lock _publishing = new();

    private IReadOnlyList<TaskRow> _allRows = [];

    /// <summary>
    /// Every task in the account as rows, for search. Built on the first keystroke rather than on
    /// every publish: it is a second full projection of the account, and most publishes happen with
    /// the search box empty.
    /// </summary>
    private IReadOnlyList<TaskRow>? _searchableRows;

    /// <summary>The snapshot the current rows came from, so the search rows can be built from it later.</summary>
    private ModelSnapshot? _projectedFrom;

    /// <summary>When the last sync succeeded, for "Synced 12s ago". Null until one has.</summary>
    private DateTimeOffset? _lastSyncedAt;

    /// <summary>When a rate-limited loop may try again, so the status bar can count it down.</summary>
    private DateTimeOffset? _pausedUntil;

    /// <summary>Consecutive rate-limit refusals, which is what the backoff grows on.</summary>
    private int _rateLimitStreak;

    private bool _syncing;
    private bool _reconnectNeeded;

    public MainPresenter(SyncEngine engine, QuickAddParser parser, IClock? clock = null)
    {
        _engine = engine;
        _parser = parser;
        _clock = clock ?? new SystemClock();
        Publish(); // reflect whatever the engine already has loaded
    }

    /// <summary>Raised whenever the sidebar, rows or status have been refreshed.</summary>
    public event Action? RowsChanged;

    /// <summary>
    /// Raised when only <see cref="Status"/> has moved. Kept apart from <see cref="RowsChanged"/> so
    /// a sync starting and finishing doesn't repaint a five-thousand-row outline to change one word.
    /// </summary>
    public event Action? StatusChanged;

    public IReadOnlyList<TaskRow> Rows { get; private set; } = [];

    public IReadOnlyList<SidebarNode> Sidebar { get; private set; } = [];

    /// <summary>Active tasks due today or overdue — what the tray icon badges.</summary>
    public int DueToday { get; private set; }

    public ViewSelection Selection { get; private set; } = ViewSelection.Default;

    public string Status { get; private set; } = string.Empty;

    /// <summary>Where the sync loop stands, for a status bar that wants to style it rather than print it.</summary>
    public SyncStatus SyncStatus { get; private set; } = new(SyncState.Never);

    public bool IsOffline { get; private set; }

    /// <summary>Whether the outline is also showing completed tasks.</summary>
    public bool ShowingCompleted { get; private set; }

    /// <summary>
    /// True when the account has more completed history than the fetch was willing to page through,
    /// so the view can say the list is the most recent rather than all of it.
    /// </summary>
    public bool CompletedTruncated { get; private set; }

    public bool CanUndo => _engine.CanUndo;

    /// <summary>Free-text filter applied to the rendered rows.</summary>
    public string SearchQuery { get; private set; } = string.Empty;

    /// <summary>
    /// The query of the selected saved filter when Termyn can't evaluate it, so the view can offer
    /// to open it in Todoist. Null whenever the current view is showing a real answer.
    /// </summary>
    public string? UnsupportedFilter { get; private set; }

    /// <summary>
    /// Every label in the account, in sidebar order. Duplicates by name are kept, unlike the
    /// sidebar's own list — the picker needs to show what is actually there.
    /// </summary>
    public IReadOnlyList<Label> Labels { get; private set; } = [];

    /// <summary>
    /// Publishes the cached model immediately, then reconciles with the server and publishes again.
    /// Losing the network leaves the cached rows on screen; only a rejected token propagates.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsOffline = false;
        Publish();
        await SyncAsync(ct);
    }

    /// <summary>Reconciles with the server and republishes, keeping the current view if offline.</summary>
    /// <returns>What the sync loop should do next — come straight back, or hold off.</returns>
    public async Task<SyncOutcome> SyncAsync(CancellationToken ct = default)
    {
        TimeSpan? pause = null;

        _syncing = true;
        PublishStatus();

        try
        {
            await _engine.SyncAsync(ct);
            IsOffline = false;
            _lastSyncedAt = _clock.UtcNow;
            _pausedUntil = null;
            _rateLimitStreak = 0;
        }
        catch (TodoistRateLimitException ex)
        {
            // Being refused is not being offline: the cached view is current, and the only thing to
            // do is wait. Honour what the server asked for, and grow our own wait when it didn't say.
            pause = Pause(ex);
        }
        catch (TodoistNetworkException)
        {
            IsOffline = true;
        }
        catch (TodoistAuthException)
        {
            _reconnectNeeded = true;
            throw;
        }
        finally
        {
            _syncing = false;

            // A rejected token empties the cache and then propagates, so this has to publish on the
            // way out too: otherwise the view keeps the last account's tasks, and its plan keeps
            // saying reminders are allowed.
            Publish();
        }

        // Only ask for another round while the network is answering, or the loop would spin.
        return new SyncOutcome(!IsOffline && pause is null && _engine.PendingCount > 0, pause);
    }

    /// <summary>Where the backoff stops growing: a shade over four minutes, inside the spec's cadence.</summary>
    private const int MaxBackoffSteps = 8;

    /// <summary>The longest Termyn will hold off for, however long the server asks.</summary>
    private static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Notes a rate limit and works out how long to wait for.
    /// </summary>
    /// <remarks>
    /// Capped, including when the server names the figure. Nothing wakes the loop out of a pause —
    /// that is the point of it — so an unbounded <c>Retry-After</c> from a misbehaving proxy would
    /// park background sync, F5 and the tray's "Sync now" for the rest of the session, with queued
    /// writes and no way to flush them short of restarting.
    /// </remarks>
    private TimeSpan Pause(TodoistRateLimitException ex)
    {
        _rateLimitStreak = Math.Min(_rateLimitStreak + 1, MaxBackoffSteps);

        var asked = ex.RetryAfter ?? Backoff(_rateLimitStreak);
        var pause = asked > MaxPause ? MaxPause : asked;

        _pausedUntil = _clock.UtcNow + pause;
        IsOffline = false;
        return pause;
    }

    /// <summary>
    /// How long to wait after a rate limit the server gave no advice about. Doubles per consecutive
    /// refusal, with jitter so several clients that started together don't come back together.
    /// </summary>
    private static TimeSpan Backoff(int step)
    {
        var seconds = Math.Min(Math.Pow(2, step), 300);
        var jitter = Random.Shared.NextDouble() * 0.25 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    /// <summary>
    /// Captures a task from quick-add text. Online, the server parses it so the result matches the
    /// web app; offline, the bounded local grammar is used and the task syncs later.
    /// </summary>
    public async Task CaptureAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var captured = await _engine.QuickAddOnlineAsync(text, ct);
        IsOffline = !captured;

        if (!captured)
        {
            var (parse, projectId, sectionId) = Resolve(text);

            // With nothing named at all, a capture belongs wherever the user is looking. Applied
            // together: a section from one view with a project named in the text is not a real place.
            if (projectId is null && sectionId is null)
            {
                projectId = Selection.ProjectId ?? SectionProjectId();
                sectionId = Selection.SectionId;
            }

            _engine.AddItem(ItemFields.ForAdd(parse, projectId, sectionId));
        }

        Publish();
    }

    /// <summary>
    /// The preview line for some capture text, empty when there is nothing typed. Both capture boxes
    /// show the same thing, so neither of them owns the wording or the blank rule.
    /// </summary>
    public string PreviewText(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : CapturePreviewText.For(Preview(text));

    /// <summary>Shows what the local parser understood, for the capture preview.</summary>
    public CapturePreview Preview(string text)
    {
        var (parse, projectId, sectionId) = Resolve(text);
        return new CapturePreview(
            parse,
            parse.ProjectName is null || projectId is not null,
            parse.SectionName is null || sectionId is not null);
    }

    // ---- Completed tasks -------------------------------------------------------------------------

    /// <summary>
    /// Shows or hides completed tasks. Turning it on fetches them — incremental sync never carries
    /// them — and turning it off drops the fetch, since nothing would ever tell it that it had gone
    /// stale.
    /// </summary>
    /// <returns>False when the fetch couldn't be made, so the caller can say why.</returns>
    public async Task<bool> ToggleCompletedAsync(CancellationToken ct = default)
    {
        if (ShowingCompleted)
        {
            ShowingCompleted = false;
            CompletedTruncated = false;
            _engine.ClearCompleted();
            Publish();
            return true;
        }

        try
        {
            var fetch = await _engine.FetchCompletedAsync(ct);
            CompletedTruncated = fetch.Truncated;
            ShowingCompleted = true;
            IsOffline = false;
        }
        catch (TodoistRateLimitException ex)
        {
            // Being refused is not being unreachable: the cached view is current and the network is
            // fine. Reported as offline it would have read as "showing cached", which is wrong.
            Pause(ex);
            return false;
        }
        catch (TodoistNetworkException)
        {
            // Nothing to show and no way to get it. Left off rather than switched on and empty,
            // which would read as "you have completed nothing".
            IsOffline = true;
            return false;
        }
        catch (TodoistAuthException)
        {
            // The engine cleared the token and purged the cache before rethrowing, so the rows on
            // screen belong to an account we no longer hold. Publishing is what takes them off.
            _reconnectNeeded = true;
            throw;
        }
        finally
        {
            Publish();
        }

        return true;
    }

    /// <summary>Reopens a completed task, moving it back among the active ones.</summary>
    public void Reopen(string id)
    {
        _engine.ReopenItem(id);
        Publish();
    }

    // ---- Navigation ----------------------------------------------------------------------------

    public void Select(ViewSelection selection)
    {
        Selection = selection;
        SelectedKey = selection.Key;
        Publish();
    }

    /// <summary>
    /// The sidebar row that is selected, which is not the same as the selection: a favourited project
    /// appears twice, and the two rows are told apart by their key alone.
    /// </summary>
    public string SelectedKey { get; private set; } = ViewSelection.Default.Key;

    /// <summary>
    /// Opens the view a sidebar key names — how a remembered selection is restored across a restart.
    /// </summary>
    /// <returns>False when nothing in the sidebar has that key, so the caller can leave things be.</returns>
    public bool SelectByKey(string? key)
    {
        if (key is null || Sidebar.FirstOrDefault(n => n.Key == key) is not { } node || node.Kind == SidebarKind.Header)
            return false;

        Selection = SelectionOf(node);
        SelectedKey = node.Key;
        Publish();
        return true;
    }

    /// <summary>
    /// Moves to the next or previous view, skipping the group labels and stopping at either end.
    /// </summary>
    /// <remarks>
    /// Stepped by key rather than by selection, so the Favourites copy of a project is a row of its
    /// own: stepping by selection jumped to that project's other copy down in the tree, and there was
    /// no way to walk through the Favourites group at all.
    /// </remarks>
    /// <returns>False when there was nowhere to move to.</returns>
    public bool SelectAdjacent(int offset)
    {
        var rows = Sidebar.Where(n => n.Kind != SidebarKind.Header).ToList();
        if (rows.Count == 0)
            return false;

        var current = rows.FindIndex(n => n.Key == SelectedKey);
        var next = current < 0 ? 0 : Math.Clamp(current + offset, 0, rows.Count - 1);
        if (next == current)
            return false;

        var node = rows[next];
        Selection = SelectionOf(node);
        SelectedKey = node.Key;
        Publish();
        return true;
    }

    /// <summary>The view a sidebar row opens.</summary>
    public static ViewSelection SelectionOf(SidebarNode node) => node.Kind switch
    {
        SidebarKind.SmartView => ViewSelection.Of(node.View ?? SmartView.Today),
        SidebarKind.Section => ViewSelection.OfSection(node.Id),
        SidebarKind.Label => ViewSelection.OfLabel(node.Id),
        SidebarKind.Filter => ViewSelection.OfFilter(node.Id),
        _ => ViewSelection.OfProject(node.Id),
    };

    public void Search(string query)
    {
        SearchQuery = query ?? string.Empty;
        Republish();
    }

    // ---- Task intents --------------------------------------------------------------------------

    public void Rename(string id, string content)
    {
        _engine.UpdateItem(id, new JsonObject { ["content"] = content });
        Publish();
    }

    public void SetPriority(string id, Priority priority)
    {
        _engine.UpdateItem(id, new JsonObject { ["priority"] = PriorityMap.ToApi(priority) });
        Publish();
    }

    /// <summary>Sets or, with a null date, clears the due date.</summary>
    public void SetDue(string id, DateOnly? date, TimeOnly? time = null)
    {
        _engine.UpdateItem(id, new JsonObject { ["due"] = ItemFields.Due(date, time) });
        Publish();
    }

    /// <summary>
    /// Sets a task's due date from whatever the user typed, clearing it when that is nothing.
    /// </summary>
    /// <remarks>
    /// A date the local grammar can read is sent as a date, so it still means the right day with no
    /// network. Anything else goes as the words themselves, for the server to read the way the web
    /// app would — which covers both a recurrence and the phrasings the bounded local grammar was
    /// never meant to cover.
    /// </remarks>
    public void SetDueFromText(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetDue(id, null);
            return;
        }

        var parse = _parser.Parse(text);

        // Only resolve locally when the grammar accounted for every word. Anything left over is the
        // tell that it didn't understand the phrase — "daily 9am" and "each monday" both yield a
        // date while dropping the repeat on the floor, and "every day p1 9am" leaves a time behind
        // that reads as this morning. Leftovers go to the server as words.
        if (!parse.IsRecurrence && parse.Content.Length == 0 && parse.DueDate is { } date)
        {
            SetDue(id, date, parse.DueTime);
            return;
        }

        _engine.SetItemDueString(id, text, parse.IsRecurrence || StartsARepeat(text));
        Publish();
    }

    /// <summary>The words a repeating schedule tends to open with, beyond the <c>every</c> the parser knows.</summary>
    private static readonly string[] RepeatStarters = ["daily", "weekly", "monthly", "yearly", "annually", "each"];

    /// <summary>
    /// Whether typed text looks like a repeat. Only a hint, and only worth having until the server
    /// answers: without it a task set to "daily 9am" looks ordinary to the close that follows, and
    /// gets ticked off instead of advanced.
    /// </summary>
    /// <remarks>
    /// Deliberately not in the parser, which also reads captured task text — "Write daily report"
    /// is a title, not a schedule. Here the whole input is the schedule, so the first word can be
    /// taken at face value.
    /// </remarks>
    private static bool StartsARepeat(string text)
    {
        var first = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && RepeatStarters.Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    public void Complete(string id)
    {
        _engine.CompleteItem(id);
        Publish();
    }

    public void Delete(string id)
    {
        _engine.DeleteItem(id);
        Publish();
    }

    public bool Undo()
    {
        var undone = _engine.Undo();
        if (undone)
            Publish();
        return undone;
    }

    /// <summary>Moves a task one place up or down among its siblings.</summary>
    /// <returns>False when it was already at that end, so the caller can skip a needless sync.</returns>
    public bool Move(string id, int offset)
    {
        var moved = _engine.MoveItem(id, offset);
        if (moved)
            Publish();
        return moved;
    }

    /// <summary>Makes a task a child of the one above it.</summary>
    public bool Indent(string id)
    {
        var indented = _engine.IndentItem(id);
        if (indented)
            Publish();
        return indented;
    }

    /// <summary>Promotes a sub-task alongside its parent.</summary>
    public bool Outdent(string id)
    {
        var outdented = _engine.OutdentItem(id);
        if (outdented)
            Publish();
        return outdented;
    }

    // ---- Reminder intents ----------------------------------------------------------------------

    /// <summary>
    /// Whether the account's plan allows reminders. False until the first sync has said otherwise,
    /// so the UI offers nothing it would then have to take back.
    /// </summary>
    public bool RemindersAvailable { get; private set; }

    /// <summary>The plan the account is on, or empty until a sync has said. Not the upgrade target.</summary>
    public string PlanName { get; private set; } = string.Empty;

    /// <summary>Whether the account still holds this task, whatever the current view happens to show.</summary>
    public bool HasTask(string id) => _engine.Snapshot().Items.Any(i => i.Id == id);

    /// <summary>
    /// The reminders on a task: the ones tied to its due date first, longest warning to shortest,
    /// then the ones set for a moment of their own.
    /// </summary>
    public IReadOnlyList<Reminder> RemindersFor(string itemId)
        => _engine.Snapshot().Reminders
            .Where(r => r.ItemId == itemId)
            .OrderBy(r => r.Kind)
            .ThenByDescending(r => r.MinuteOffset)
            .ToList();

    /// <summary>Adds a reminder a number of minutes before the task is due.</summary>
    /// <returns>False when the plan won't take it, so the caller can say why rather than failing later.</returns>
    public bool AddRelativeReminder(string itemId, int minutesBefore)
    {
        if (!CanAddReminder())
            return false;

        var added = _engine.AddRelativeReminder(itemId, minutesBefore) is not null;
        if (added)
            Publish();
        return added;
    }

    /// <summary>Adds a reminder for a fixed moment, whatever the task's own due date is.</summary>
    public bool AddAbsoluteReminder(string itemId, DateOnly date, TimeOnly time)
    {
        if (!CanAddReminder())
            return false;

        var added = _engine.AddAbsoluteReminder(itemId, date, time) is not null;
        if (added)
            Publish();
        return added;
    }

    /// <summary>
    /// Whether another time-based reminder would be accepted. The plan caps how many the account
    /// may hold, and a save the server refuses is the one thing this UI is meant never to offer.
    /// </summary>
    private bool CanAddReminder()
    {
        var snapshot = _engine.Snapshot();
        if (!snapshot.RemindersAvailable)
            return false;

        var cap = snapshot.PlanLimits?.MaxTimeReminders ?? 0;
        if (cap <= 0)
            return true; // no cap reported, so nothing to check it against

        return snapshot.Reminders.Count(r => r.Kind is not ReminderKind.Location) < cap;
    }

    public void DeleteReminder(string id)
    {
        _engine.DeleteReminder(id);
        Publish();
    }

    // ---- Label intents -------------------------------------------------------------------------

    /// <summary>
    /// Every label of this name. Nothing stops an account holding two — offline creates racing, or
    /// a rename onto an existing name — and to a task they are indistinguishable, since a task
    /// names its labels rather than pointing at them. So the sidebar shows one row and operations
    /// on it apply to all of them; acting on whichever happened to be enumerated first would leave
    /// the other behind, still carrying the name.
    /// </summary>
    private List<Label> LabelsNamed(string name)
        => Labels.Where(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Replaces the labels on a task with the given set.</summary>
    public void SetLabels(string id, IReadOnlyList<string> labels)
    {
        _engine.SetItemLabels(id, labels);
        Publish();
    }

    /// <summary>Adds a label to the account if it isn't there already.</summary>
    /// <returns>The label's name, or <c>null</c> when the name was blank and nothing was added.</returns>
    public string? AddLabel(string name)
    {
        // A nameless label is one the server will reject, and a rejected command retries to its
        // ceiling and then sits in the outbox as a failure the user can do nothing about.
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return null;

        var existing = _engine.Snapshot().Labels
            .FirstOrDefault(l => string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            return existing.Name;

        _engine.AddLabel(trimmed);
        Publish();
        return trimmed;
    }

    /// <summary>Renames every label of <paramref name="name"/>, and follows it if it is in view.</summary>
    public void RenameLabel(string name, string newName)
    {
        var labels = LabelsNamed(name);
        if (labels.Count == 0)
            return;

        foreach (var label in labels)
            _engine.RenameLabel(label.Id, newName);

        // A label view is held by name, so a rename moves it. Without this the selection names a
        // label the account no longer has: nothing highlighted, and an outline that empties itself
        // the moment the server carries the rename across to the tasks.
        if (string.Equals(Selection.LabelName, name, StringComparison.OrdinalIgnoreCase))
            Selection = ViewSelection.OfLabel(newName);

        Publish();
    }

    /// <summary>Favourites or unfavourites every label of this name, so the sidebar row is coherent.</summary>
    public void ToggleLabelFavorite(string name)
    {
        var labels = LabelsNamed(name);
        if (labels.Count == 0)
            return;

        // The row shows a star when any of them is favourited, so that is what it toggles off.
        var favourite = !labels.Any(l => l.IsFavorite);
        foreach (var label in labels)
            _engine.SetLabelFavorite(label.Id, favourite);

        Publish();
    }

    public void DeleteLabel(string name)
    {
        var labels = LabelsNamed(name);
        if (labels.Count == 0)
            return;

        foreach (var label in labels)
            _engine.DeleteLabel(label.Id);

        // Don't leave the outline showing a label that no longer exists.
        if (string.Equals(Selection.LabelName, name, StringComparison.OrdinalIgnoreCase))
            Selection = ViewSelection.Default;

        Publish();
    }

    // ---- Structure intents ---------------------------------------------------------------------

    public void AddProject(string name)
    {
        _engine.AddProject(name);
        Publish();
    }

    public void RenameProject(string id, string name)
    {
        _engine.RenameProject(id, name);
        Publish();
    }

    public void ToggleProjectFavorite(string id)
    {
        var current = _engine.Snapshot().Projects.FirstOrDefault(p => p.Id == id);
        if (current is null)
            return;

        _engine.SetProjectFavorite(id, !current.IsFavorite);
        Publish();
    }

    public void DeleteProject(string id)
    {
        // Captured first: the delete cascades to descendants, so afterwards there's no way to tell
        // whether the selected section or project belonged to what just went.
        var doomed = DescendantProjects(id);
        var doomedSections = _engine.Snapshot().Sections
            .Where(s => s.ProjectId is { } p && doomed.Contains(p))
            .Select(s => s.Id)
            .ToHashSet();

        _engine.DeleteProject(id);

        // Don't leave the outline pointed at something that no longer exists.
        if ((Selection.ProjectId is { } selected && doomed.Contains(selected))
            || (Selection.SectionId is { } section && doomedSections.Contains(section)))
        {
            Selection = ViewSelection.Default;
        }

        Publish();
    }

    private HashSet<string> DescendantProjects(string id)
        => ProjectTree.WithDescendants(_engine.Snapshot().Projects, [id]);

    public void AddSection(string name, string projectId)
    {
        _engine.AddSection(name, projectId);
        Publish();
    }

    public void RenameSection(string id, string name)
    {
        _engine.RenameSection(id, name);
        Publish();
    }

    public void DeleteSection(string id)
    {
        _engine.DeleteSection(id);

        if (Selection.SectionId == id)
            Selection = ViewSelection.Default;

        Publish();
    }

    // ---- Building the view ---------------------------------------------------------------------

    private string? SectionProjectId()
        => Selection.SectionId is { } id
            ? _engine.Snapshot().Sections.FirstOrDefault(s => s.Id == id)?.ProjectId
            : null;

    private (QuickAddParse Parse, string? ProjectId, string? SectionId) Resolve(string text)
    {
        var parse = _parser.Parse(text);

        // Every word was a token, so there is no task text left. Keep the raw input rather than
        // creating a blank task the server would reject and silently discard.
        if (string.IsNullOrWhiteSpace(parse.Content))
            parse = parse with { Content = text.Trim() };

        // An ambiguous name is treated as unresolved: filing the task under whichever project
        // happened to be enumerated first would be a guess.
        string? projectId = null;
        if (parse.ProjectName is not null)
        {
            var projects = _engine.FindProjectsByName(parse.ProjectName);
            if (projects.Count == 1)
                projectId = projects[0].Id;
        }

        string? sectionId = null;

        // A named project that didn't resolve means we don't know where this task belongs, so a
        // section matching by name alone would file it somewhere the user never asked for.
        var projectKnown = parse.ProjectName is null || projectId is not null;
        if (parse.SectionName is not null && projectKnown)
        {
            var sections = _engine.FindSectionsByName(parse.SectionName, projectId);
            if (sections.Count == 1)
            {
                // A bare section implies its project; sending one without the other is invalid, so
                // a section that doesn't name its own project is no use to us.
                if (projectId is not null)
                    sectionId = sections[0].Id;
                else if (sections[0].ProjectId is { } owner)
                {
                    sectionId = sections[0].Id;
                    projectId = owner;
                }
            }
        }

        return (parse, projectId, sectionId);
    }

    /// <summary>Re-reads the model and republishes. Call after anything that mutates the engine.</summary>
    private void Publish()
    {
        lock (_publishing)
        {
            var snapshot = _engine.Snapshot();

            // Cleared before the outline is built, which is what decides whether it gets set again.
            UnsupportedFilter = null;

            Labels = snapshot.Labels.OrderBy(l => l.ItemOrder).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            RemindersAvailable = snapshot.RemindersAvailable;
            PlanName = snapshot.PlanLimits?.PlanName ?? string.Empty;
            Sidebar = BuildSidebar(snapshot);

            // The selected row can go — deleted here, or removed by a sync — and every path that
            // falls the selection back to a default would otherwise have to remember to move the key
            // with it.
            if (Sidebar.Count > 0 && Sidebar.All(n => n.Key != SelectedKey))
                SelectedKey = Selection.Key;

            _allRows = BuildOutline(snapshot, scoped: true);

            _projectedFrom = snapshot;
            _searchableRows = null;

            ApplyFilter(snapshot.PendingCount, snapshot.FailedCount);
        }

        RowsChanged?.Invoke();
    }

    /// <summary>Re-applies the search filter to the rows already projected.</summary>
    private void Republish()
    {
        lock (_publishing)
            ApplyFilter(_engine.PendingCount, _engine.FailedCount);

        RowsChanged?.Invoke();
    }

    private List<SidebarNode> BuildSidebar(ModelSnapshot snapshot)
    {
        var today = snapshot.Today;
        var inbox = snapshot.InboxProjectId;
        var active = VisibleItems(snapshot);

        // Archived projects and sections are still returned by sync; they don't belong in the sidebar.
        var projects = snapshot.Projects.Where(p => !p.IsArchived && p.Id.Length > 0).ToList();
        var sections = snapshot.Sections.Where(s => !s.IsArchived && s.Id.Length > 0).ToList();

        // Every count in the sidebar comes off one pass. Counting per node instead would be a scan
        // of the whole account per project and per section, on every publish — and a sync publishes.
        var todayCount = 0;
        var upcomingCount = 0;
        var inboxCount = 0;
        var byProject = new Dictionary<string, int>(StringComparer.Ordinal);
        var bySection = new Dictionary<string, int>(StringComparer.Ordinal);
        var byLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var horizon = today.AddDays(SmartViews.UpcomingDays);

        foreach (var item in active)
        {
            // Resolved once and compared twice. Asking the two predicates separately parsed every
            // task's due date twice per publish, and a publish happens on every keystroke.
            if (SmartViews.DueOn(item, snapshot.TimeZone) is { } due)
            {
                if (due <= today) todayCount++;
                else if (due <= horizon) upcomingCount++;
            }

            if (SmartViews.IsInbox(item, inbox)) inboxCount++;

            if (item.ProjectId is { } itemProject)
                byProject[itemProject] = byProject.GetValueOrDefault(itemProject) + 1;
            if (item.SectionId is { } itemSection)
                bySection[itemSection] = bySection.GetValueOrDefault(itemSection) + 1;

            foreach (var label in item.Labels)
                byLabel[label] = byLabel.GetValueOrDefault(label) + 1;
        }

        // Kept for the tray badge, which wants the same number the sidebar shows and shouldn't have
        // to read it back out of a rendered row label.
        DueToday = todayCount;

        var nodes = new List<SidebarNode>
        {
            View(SmartView.Today, "Today", todayCount),
            View(SmartView.Upcoming, "Upcoming", upcomingCount),
            View(SmartView.Inbox, "Inbox", inboxCount),
        };

        // Already sorted by Publish; the sidebar only drops the ones it can't address.
        var labels = Labels.Where(l => l.Id.Length > 0).ToList();

        var filters = snapshot.Filters
            .Where(f => f.Id.Length > 0)
            .OrderBy(f => f.ItemOrder)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Favourites are grouped by kind — projects, then labels, then filters — each in its own
        // order. Interleaving them would put three unrelated order fields in one sequence.
        var favouriteProjects = projects
            .Where(p => p.IsFavorite)
            .OrderBy(p => p.ChildOrder)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        // Deduped for the same reason the Labels group below is: two labels of one name are one
        // view, and two rows sharing a key are two rows the tree can't tell apart.
        var favouriteLabels = labels.Where(l => l.IsFavorite).DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var favouriteFilters = filters.Where(f => f.IsFavorite).ToList();

        if (favouriteProjects.Count + favouriteLabels.Count + favouriteFilters.Count > 0)
        {
            nodes.Add(Header("Favourites"));

            // Keyed apart from their copies further down, so clicking one doesn't select the other.
            foreach (var favorite in favouriteProjects)
                nodes.Add(new SidebarNode(SidebarKind.Project, favorite.Id, favorite.Name, 1,
                    Key: SidebarKeys.Favourite(SidebarKind.Project, favorite.Id), IsFavorite: true, Count: byProject.GetValueOrDefault(favorite.Id)));

            foreach (var favorite in favouriteLabels)
                nodes.Add(new SidebarNode(SidebarKind.Label, favorite.Name, favorite.Name, 1,
                    Key: SidebarKeys.Favourite(SidebarKind.Label, favorite.Name), IsFavorite: true, Count: byLabel.GetValueOrDefault(favorite.Name)));

            foreach (var favorite in favouriteFilters)
                nodes.Add(new SidebarNode(SidebarKind.Filter, favorite.Id, favorite.Name, 1,
                    Key: SidebarKeys.Favourite(SidebarKind.Filter, favorite.Id), IsFavorite: true));
        }

        nodes.Add(Header("Projects"));

        // Projects nest, and each carries its sections beneath it.
        var byParent = projects
            .GroupBy(p => p.ParentId ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.ChildOrder).ThenBy(p => p.Id, StringComparer.Ordinal).ToList());

        var listed = new HashSet<string>();
        AddProjects(string.Empty, 1);

        if (labels.Count > 0)
        {
            nodes.Add(Header("Labels"));

            // Selected by name, since that is how a task refers to a label. Two labels sharing a
            // name are the same view, so they are listed once — starred if either of them is, or
            // the row would contradict the copy of itself under Favourites.
            var starred = labels.Where(l => l.IsFavorite).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var label in labels.DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
                nodes.Add(new SidebarNode(SidebarKind.Label, label.Name, label.Name, 1,
                    Key: SidebarKeys.For(SidebarKind.Label, label.Name), IsFavorite: starred.Contains(label.Name), Count: byLabel.GetValueOrDefault(label.Name)));
        }

        if (filters.Count > 0)
        {
            nodes.Add(Header("Filters"));

            // No count: unlike the others it can't be had from the single pass above, and running
            // every saved query over every task on each publish is not worth a number in brackets.
            foreach (var filter in filters)
                nodes.Add(new SidebarNode(SidebarKind.Filter, filter.Id, filter.Name, 1,
                    Key: SidebarKeys.For(SidebarKind.Filter, filter.Id), IsFavorite: filter.IsFavorite));
        }

        return nodes;

        void AddProjects(string parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var children))
                return;

            foreach (var project in children)
            {
                // A parent cycle in the data would otherwise recurse until the stack goes.
                if (!listed.Add(project.Id))
                    continue;

                nodes.Add(new SidebarNode(SidebarKind.Project, project.Id, project.Name, depth,
                    Key: SidebarKeys.For(SidebarKind.Project, project.Id), IsFavorite: project.IsFavorite, Count: byProject.GetValueOrDefault(project.Id)));

                var owned = sections
                    .Where(s => s.ProjectId == project.Id)
                    .OrderBy(s => s.SectionOrder)
                    .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);

                foreach (var section in owned)
                    nodes.Add(new SidebarNode(SidebarKind.Section, section.Id, section.Name, depth + 1,
                        Key: SidebarKeys.For(SidebarKind.Section, section.Id), Count: bySection.GetValueOrDefault(section.Id)));

                AddProjects(project.Id, depth + 1);
            }
        }

        SidebarNode View(SmartView view, string label, int count)
            => new(SidebarKind.SmartView, view.ToString(), label, 0, Key: SidebarKeys.For(SidebarKind.SmartView, view.ToString()), View: view, Count: count);

        SidebarNode Header(string label)
            => new(SidebarKind.Header, label, label, 0, Key: SidebarKeys.For(SidebarKind.Header, label));
    }

    /// <summary>Active tasks that aren't filed under an archived project.</summary>
    private static List<TaskItem> VisibleItems(ModelSnapshot snapshot)
    {
        var archived = snapshot.Projects.Where(p => p.IsArchived).Select(p => p.Id).ToHashSet();
        return snapshot.Items
            .Where(i => !i.Completed && (i.ProjectId is null || !archived.Contains(i.ProjectId)))
            .ToList();
    }

    /// <summary>
    /// Flattens the tasks in the current selection into an outline, depth-first, so a sub-task
    /// appears under its parent. A task whose parent isn't in view becomes a root of its own.
    /// </summary>
    private List<TaskRow> BuildOutline(ModelSnapshot snapshot, bool scoped)
    {
        var projects = snapshot.Projects.DistinctBy(p => p.Id).ToDictionary(p => p.Id, p => p.Name);

        // Counted once for the whole outline rather than looked up per row.
        var reminderCounts = snapshot.Reminders
            .Where(r => r.ItemId is not null)
            .GroupBy(r => r.ItemId!)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var selected = scoped ? InSelection(snapshot) : _ => true;
        var visible = VisibleItems(snapshot)
            .Where(i => i.Id.Length > 0 && selected(i))
            .ToList();
        var present = visible.Select(i => i.Id).ToHashSet();

        var byParent = visible
            .GroupBy(i => i.ParentId is { } p && present.Contains(p) ? p : string.Empty)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(i => i.ProjectId ?? string.Empty, StringComparer.Ordinal)
                      .ThenBy(i => i.ChildOrder)
                      .ThenBy(i => i.Id, StringComparer.Ordinal)
                      .ToList());

        var rows = new List<TaskRow>(visible.Count);
        var emitted = new HashSet<string>();
        Emit(string.Empty, 0);

        if (ShowingCompleted)
            rows.AddRange(CompletedRows(snapshot, selected, Row));

        return rows;

        void Emit(string parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var children))
                return;

            foreach (var item in children)
            {
                // A parent cycle in the data would otherwise recurse until the stack goes.
                if (!emitted.Add(item.Id))
                    continue;

                rows.Add(Row(item, depth));
                Emit(item.Id, depth + 1);
            }
        }

        TaskRow Row(TaskItem item, int depth) => new(
            item.Id,
            item.Content,
            item.Priority,
            item.ProjectId is not null && projects.TryGetValue(item.ProjectId, out var name) ? name : string.Empty,
            item.DueText ?? item.DueDate ?? string.Empty,
            item.Labels,
            depth,
            item.IsRecurring,
            reminderCounts.GetValueOrDefault(item.Id),
            item.Completed);
    }

    /// <summary>
    /// The completed tasks belonging to the current view, most recently finished first, flat. They
    /// go below the active ones rather than in among them: a task's place in the outline comes from
    /// its sibling order, which stops meaning anything once it is done.
    /// </summary>
    /// <summary>
    /// Stands in for the completion time of a task ticked off here and not yet acked. Sorts above
    /// every real ISO timestamp, which is where a task finished a moment ago belongs.
    /// </summary>
    private const string JustNow = "￿";

    private static List<TaskRow> CompletedRows(
        ModelSnapshot snapshot,
        Func<TaskItem, bool> selected,
        Func<TaskItem, int, TaskRow> row)
    {
        var archived = snapshot.Projects.Where(p => p.IsArchived).Select(p => p.Id).ToHashSet();

        return snapshot.CompletedItems
            .Where(i => i.Id.Length > 0 && (i.ProjectId is null || !archived.Contains(i.ProjectId)))
            .Where(selected)
            // Ordinal on the server's own ISO timestamps, which sort as text. A task ticked off here
            // has none until the server acks, and it was finished a moment ago — so it stands in for
            // a timestamp that sorts above every real one, rather than the empty string, which would
            // drop it to the bottom of three months of history.
            .OrderByDescending(i => i.CompletedAt ?? JustNow, StringComparer.Ordinal)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .Select(i => row(i, 0))
            .ToList();
    }

    /// <summary>
    /// The test for "is this task in the current view", resolved once per publish. A filter has to
    /// be parsed and its projects indexed, which is far too much work to repeat per task.
    /// </summary>
    private Func<TaskItem, bool> InSelection(ModelSnapshot snapshot)
    {
        if (Selection.SectionId is { } sectionId)
            return item => item.SectionId == sectionId;

        if (Selection.ProjectId is { } projectId)
            return item => item.ProjectId == projectId;

        if (Selection.LabelName is { } label)
            return item => item.Labels.Contains(label, StringComparer.OrdinalIgnoreCase);

        if (Selection.FilterId is { } filterId)
            return FilterPredicate(snapshot, filterId);

        return Selection.View is not { } view
            ? _ => true
            : item => SmartViews.Matches(item, view, snapshot.Today, snapshot.TimeZone, snapshot.InboxProjectId);
    }

    private Func<TaskItem, bool> FilterPredicate(ModelSnapshot snapshot, string filterId)
    {
        var filter = snapshot.Filters.FirstOrDefault(f => f.Id == filterId);
        if (filter is null)
            return _ => false;

        var vocabulary = FilterVocabulary.From(snapshot.Projects, snapshot.Labels);
        var parsed = FilterParser.Parse(filter.Query, vocabulary);

        if (!parsed.IsSupported)
        {
            // Nothing, not everything. A full task list looks like a filter that ran and matched
            // broadly, which is the mistake this whole path exists to avoid.
            UnsupportedFilter = ForDisplay(filter.Query);
            return _ => false;
        }

        var context = new FilterContext(snapshot.Projects, snapshot.Today, snapshot.TimeZone);
        return item => FilterEvaluator.Matches(parsed.Expression!, item, context);
    }

    /// <summary>
    /// A filter query cut down to something a one-line notice can hold. The query comes off the
    /// account, so it can be any length and can carry newlines that would break the line it sits on.
    /// </summary>
    private static string ForDisplay(string query)
    {
        var flat = query.ReplaceLineEndings(" ").Trim();

        // A saved filter with no query at all still has to say something, or the notice trails off
        // after the colon with nothing behind it.
        if (flat.Length == 0)
            return "(no query)";

        return flat.Length > 200 ? flat[..200] + "…" : flat;
    }

    private void ApplyFilter(int pending, int failed)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            Rows = _allRows;
        }
        else
        {
            // Matches rarely share a parent, so a filtered result is a flat list rather than a
            // tree with most of its structure missing.
            var q = SearchQuery.Trim();
            Rows = Searchable()
                .Where(r =>
                    r.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    r.Project.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    r.Labels.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r with { Depth = 0 })
                .ToList();
        }

        Status = ComposeStatus(pending, failed);
    }

    /// <summary>
    /// Every task as rows, for search. Search runs over everything loaded rather than just the view
    /// in front of you — otherwise Ctrl+F from Today silently misses most of the account — but it
    /// costs a full projection, so it waits until something is actually typed.
    /// </summary>
    private IReadOnlyList<TaskRow> Searchable()
        => _searchableRows ??= _projectedFrom is { } snapshot ? BuildOutline(snapshot, scoped: false) : [];

    /// <summary>The whole status line: what is on screen, then where the sync loop stands.</summary>
    private string ComposeStatus(int pending, int failed)
    {
        SyncStatus = BuildSyncStatus(pending, failed);

        return string.Join(" · ",
            new[]
            {
                Rows.Count == 1 ? "1 task" : $"{Rows.Count} tasks",
                CompletedTruncated ? "most recent completed only" : null,
                SyncStatus.Describe(),
            }.Where(s => s is not null));
    }

    private SyncStatus BuildSyncStatus(int pending, int failed)
    {
        var now = _clock.UtcNow;

        // The states are mutually exclusive and ordered by what the user can do about them: a
        // rejected token needs attention whatever else is true, and a sync in flight is the most
        // recent thing that happened.
        if (_reconnectNeeded)
            return new SyncStatus(SyncState.ReconnectNeeded, null, null, pending, failed);

        if (_syncing)
            return new SyncStatus(SyncState.Syncing, null, null, pending, failed);

        if (_pausedUntil is { } until && until > now)
            return new SyncStatus(SyncState.Paused, null, until - now, pending, failed);

        if (IsOffline)
            return new SyncStatus(SyncState.Offline, null, null, pending, failed);

        return _lastSyncedAt is { } synced
            ? new SyncStatus(SyncState.Synced, now - synced, null, pending, failed)
            : new SyncStatus(SyncState.Never, null, null, pending, failed);
    }

    /// <summary>
    /// Refreshes the status line alone, leaving the rows exactly as they are — and leaving anything
    /// mid-edit in the view undisturbed, which a full publish would not.
    /// </summary>
    public void PublishStatus()
    {
        lock (_publishing)
            Status = ComposeStatus(_engine.PendingCount, _engine.FailedCount);

        StatusChanged?.Invoke();
    }

    // ---- Command palette -------------------------------------------------------------------------

    /// <summary>
    /// The palette's entries, ranked against what has been typed. Actions come before places when
    /// nothing has been typed, since an empty palette is being browsed rather than searched.
    /// </summary>
    public IReadOnlyList<PaletteEntry> Palette(string? query)
        => Fuzzy.Rank(PaletteEntries(), query);

    private IEnumerable<PaletteEntry> PaletteEntries()
    {
        yield return new PaletteEntry(PaletteKind.Action, "New task", "action", Command: PaletteCommand.NewTask);
        yield return new PaletteEntry(PaletteKind.Action, "New project", "action", Command: PaletteCommand.NewProject);
        yield return new PaletteEntry(PaletteKind.Action, "New section", "action", Command: PaletteCommand.NewSection);
        yield return new PaletteEntry(PaletteKind.Action, "Sync now", "action", Command: PaletteCommand.SyncNow);
        yield return new PaletteEntry(
            PaletteKind.Action,
            ShowingCompleted ? "Hide completed tasks" : "Show completed tasks",
            "action",
            Command: PaletteCommand.ToggleCompleted);
        yield return new PaletteEntry(PaletteKind.Action, "Undo", "action", Command: PaletteCommand.Undo);
        yield return new PaletteEntry(PaletteKind.Action, "Settings", "action", Command: PaletteCommand.Settings);
        yield return new PaletteEntry(PaletteKind.Action, "Check for updates", "action", Command: PaletteCommand.CheckForUpdates);
        yield return new PaletteEntry(PaletteKind.Action, "About Termyn", "action", Command: PaletteCommand.About);

        // Built from the sidebar rather than the model, so the palette reaches exactly what the tree
        // does — same names, same order, and nothing archived or unaddressable.
        foreach (var node in Sidebar)
        {
            var entry = node.Kind switch
            {
                SidebarKind.SmartView when node.View is { } view
                    => new PaletteEntry(PaletteKind.SmartView, node.Label, "view", ViewSelection.Of(view)),
                SidebarKind.Project
                    => new PaletteEntry(PaletteKind.Project, node.Label, "project", ViewSelection.OfProject(node.Id)),
                SidebarKind.Section
                    => new PaletteEntry(PaletteKind.Section, node.Label, "section", ViewSelection.OfSection(node.Id)),
                SidebarKind.Label
                    => new PaletteEntry(PaletteKind.Label, node.Label, "label", ViewSelection.OfLabel(node.Id)),
                SidebarKind.Filter
                    => new PaletteEntry(PaletteKind.Filter, node.Label, "filter", ViewSelection.OfFilter(node.Id)),
                _ => null,
            };

            // A favourited project is in the sidebar twice; the palette lists it once.
            if (entry is not null && node.Key == SidebarKeys.For(node.Kind, node.Id))
                yield return entry;
        }
    }
}
