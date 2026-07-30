using System.Text.Json;
using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Capture;
using Termyn.Core.Model;
using Termyn.Core.Platform;

namespace Termyn.Core.Sync;

/// <summary>A consistent view of the model, taken while the engine is locked.</summary>
public sealed record ModelSnapshot(
    IReadOnlyList<TaskItem> Items,
    IReadOnlyList<Project> Projects,
    IReadOnlyList<Section> Sections,
    IReadOnlyList<Label> Labels,
    IReadOnlyList<Filter> Filters,
    IReadOnlyList<Reminder> Reminders,
    PlanLimits? PlanLimits,
    DateOnly Today,
    TimeZoneInfo TimeZone,
    int PendingCount,
    int FailedCount,
    IReadOnlyList<TaskItem> CompletedItems)
{
    /// <summary>The Inbox, which tasks fall back to when they name no project.</summary>
    public string? InboxProjectId => Projects.FirstOrDefault(p => p.IsInboxProject)?.Id;

    /// <summary>
    /// Whether the account may set reminders. Not knowing counts as not allowed — offering it and
    /// having the server refuse the save is the one thing the reminder UI must not do.
    /// </summary>
    public bool RemindersAvailable => PlanLimits?.Reminders == true;
}

/// <summary>
/// Owns the offline-first sync loop: loads the snapshot, performs incremental sync, reconciles
/// server changes (upserts, tombstones, temp-id remapping) against optimistic local writes held in
/// a durable outbox, and flushes those writes as field-level commands.
/// </summary>
/// <remarks>
/// All model and store access is serialised on a single gate, so UI-thread work and the background
/// sync worker cannot race. Only the network call happens outside the gate; readers go through
/// <see cref="Snapshot"/> or one of the narrow lookups.
/// </remarks>
public sealed class SyncEngine
{
    private const int MaxCommandsPerSync = 100;
    private const int MaxUndoDepth = 50;

    /// <summary>Marker for a destructive write that cannot be reversed.</summary>
    private const string UndoBarrier = "__barrier";

    /// <summary>
    /// Command argument fields that carry a resource id: a command's own target, plus every field
    /// that points at another resource. Derived from one list so a new reference field can't be
    /// added to the model and forgotten here, which leaves commands holding dead temporary ids.
    /// </summary>
    private static readonly string[] IdKeys = ["id", .. TodoistModel.ReferenceKeys];

    private readonly object _gate = new();
    private readonly ITodoistApi _api;
    private readonly ISnapshotStore _store;
    private readonly ISecretStore _secrets;
    private readonly IClock _clock;
    private readonly int _attemptCeiling;
    private readonly List<OutboxCommand> _outbox = [];

    /// <summary>
    /// Deletions the server reported while the resource still had an un-acked local write. Tombstones
    /// arrive once, so they are held here and applied as soon as the blocking command clears.
    /// </summary>
    private readonly List<ResourceKey> _deferredDeletes = [];

    /// <summary>
    /// Completed tasks fetched on demand, keyed by id. Held only for as long as the user is looking
    /// at them: incremental sync never mentions completed tasks, so there would be nothing to tell
    /// this copy when it went stale, and nothing to tombstone it when the task was deleted.
    /// </summary>
    private readonly Dictionary<string, JsonObject> _completed = new(StringComparer.Ordinal);

    /// <summary>Uuids of destructive writes, most recent last, that <see cref="Undo"/> can reverse.</summary>
    private readonly List<string> _undoStack = [];

    /// <summary>What each undoable write did, so it can still be reversed after the server acks it.</summary>
    private readonly Dictionary<string, UndoableWrite> _undoable = [];

    /// <summary>Bumped whenever the cache is wiped, so an in-flight response can't repopulate it.</summary>
    private int _generation;

    private sealed record UndoableWrite(string Type, string Id, string? PriorJson);

    public SyncEngine(ITodoistApi api, ISnapshotStore store, ISecretStore secrets, IClock? clock = null, int attemptCeiling = 5)
    {
        _api = api;
        _store = store;
        _secrets = secrets;
        _clock = clock ?? new SystemClock();
        _attemptCeiling = attemptCeiling;
    }

    /// <summary>The raw model. Only touch this while holding the gate — readers want <see cref="Snapshot"/>.</summary>
    private TodoistModel Model { get; } = new();

    /// <summary>The token the next incremental sync will use.</summary>
    public string SyncToken
    {
        get { lock (_gate) return Model.SyncToken; }
    }

    /// <summary>Returns a copy of a resource's raw JSON, or null if it isn't held.</summary>
    public JsonObject? RawResource(string type, string id)
    {
        lock (_gate) return Model.Get(type, id)?.DeepClone().AsObject();
    }

    /// <summary>
    /// Finds a project by name. A narrow lookup rather than a full <see cref="Snapshot"/>, because
    /// the capture preview runs this on every keystroke.
    /// </summary>
    public IReadOnlyList<Project> FindProjectsByName(string name)
    {
        lock (_gate)
            return Model.Projects()
                .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>
    /// Finds the sections matching a name, optionally within one project. Section names are only
    /// unique inside a project, so the caller decides what to do when more than one comes back.
    /// </summary>
    public IReadOnlyList<Section> FindSectionsByName(string name, string? projectId)
    {
        lock (_gate)
            return Model.Sections()
                .Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                .Where(s => projectId is null || s.ProjectId == projectId)
                .ToList();
    }

    /// <summary>Takes a consistent view of the model and queue depths.</summary>
    public ModelSnapshot Snapshot()
    {
        lock (_gate)
        {
            var zone = Projections.ToTimeZone(Model.Get(ResourceType.User, ResourceType.User));
            return new ModelSnapshot(
                Model.Items().ToList(),
                Model.Projects().ToList(),
                Model.Sections().ToList(),
                Model.Labels().ToList(),
                Model.Filters().ToList(),
                Model.Reminders().ToList(),
                Model.PlanLimits(),
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.UtcNow, zone).DateTime),
                zone,
                _outbox.Count(c => c.State == OutboxState.Pending),
                _outbox.Count(c => c.State == OutboxState.Failed),
                CompletedItems());
        }
    }

    /// <summary>
    /// Every completed task known: the ones the model still holds — a task ticked off here stays in
    /// the model, flagged — plus the ones fetched on demand. The model's copy wins where both have
    /// one, since only it has the local writes applied.
    /// </summary>
    private List<TaskItem> CompletedItems()
    {
        var all = Model.Items().ToList();
        var items = all.Where(i => i.Completed).ToList();

        // Every id the model holds, not just the completed ones: a fetched task the model has as
        // active was reopened since the fetch, and must not come back under its old state.
        var known = all.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var (id, json) in _completed)
            if (!known.Contains(id))
                items.Add(Projections.ToTaskItem(json));

        return items;
    }

    public int PendingCount
    {
        get { lock (_gate) return _outbox.Count(c => c.State == OutboxState.Pending); }
    }

    public int FailedCount
    {
        get { lock (_gate) return _outbox.Count(c => c.State == OutboxState.Failed); }
    }

    public IReadOnlyList<OutboxCommand> Outbox
    {
        get { lock (_gate) return _outbox.ToList(); }
    }

    public bool CanUndo
    {
        get { lock (_gate) return _undoStack.Count > 0; }
    }

    /// <summary>
    /// Rebuilds the in-memory model and outbox from the durable snapshot. Rows that cannot be parsed
    /// are skipped rather than failing the load, so one corrupt record can't stop the app starting.
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            var snapshot = _store.Load();
            Model.Clear();
            Model.SyncToken = snapshot.SyncToken;
            _outbox.Clear();
            _deferredDeletes.Clear();
            _deferredDeletes.AddRange(snapshot.DeferredDeletes);
            _undoStack.Clear();
            _undoable.Clear();
            _completed.Clear();

            foreach (var r in snapshot.Resources)
            {
                if (TryParse(r.Json) is { } o)
                    Model.Upsert(r.Type, r.Id, o);
            }

            ResyncIfResourcesAreMissing();

            foreach (var c in snapshot.Outbox)
            {
                if (TryParse(c.ArgsJson) is null)
                    continue;
                _outbox.Add(c);

                if (c.State != OutboxState.Pending)
                    continue;

                // A queued close on a recurring task carries no prior, because it changed nothing.
                // It still belongs on the stack: dropping it would let Ctrl+Z reach past and undo
                // whatever came before instead.
                if (c.Type == "item_close" && ParseArgs(c)["id"] is JsonValue closing
                    && Model.Get(ResourceType.Items, closing.ToString()) is { } closed
                    && Projections.ToTaskItem(closed).IsRecurring)
                {
                    RecordUndoBarrier(c);
                    continue;
                }

                if (c.PriorJson is null)
                    continue;

                // Undo can't reverse a cascading delete once the server has it, and leaving it off
                // the stack entirely was worse than not restoring it: Ctrl+Z reached straight past
                // the delete to whatever came before, and undid that instead.
                if (IsCascadingDelete(c.PriorJson))
                {
                    RecordUndoBarrier(c);
                    continue;
                }

                // A completion or deletion that hasn't flushed is still reversible after a restart.
                if (c.Type is "item_close" or "item_delete" && ParseArgs(c)["id"] is JsonValue id)
                    RecordUndoable(c, id.ToString(), c.PriorJson);
            }
        }
    }

    /// <summary>Performs one incremental sync: flushes pending writes and reconciles the response.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        string token;
        string syncToken;
        int generation;
        List<OutboxCommand> pending;
        List<Command> commands;

        lock (_gate)
        {
            token = _secrets.GetToken()
                    ?? throw new InvalidOperationException("No Todoist token is stored.");
            syncToken = Model.SyncToken;
            generation = _generation;
            pending = _outbox.Where(c => c.State == OutboxState.Pending).Take(MaxCommandsPerSync).ToList();
            commands = pending.Select(c => new Command(c.Type, c.Uuid, c.TempId, ParseArgs(c))).ToList();

            // Once a command is on the wire, undoing it locally would diverge from the server.
            foreach (var c in pending)
                c.InFlight = true;
        }

        SyncResponse response;
        try
        {
            response = await _api.SyncAsync(token, syncToken, ResourceType.All, commands, ct);
        }
        catch (TodoistAuthException)
        {
            // The token is no longer usable: drop it and the cache together so a different account
            // can't be shown a previous one's tasks.
            lock (_gate)
            {
                _secrets.ClearToken();
                PurgeLocal();
            }
            throw;
        }
        catch
        {
            lock (_gate)
            {
                foreach (var c in pending)
                    c.InFlight = false;
            }
            throw;
        }

        lock (_gate)
        {
            foreach (var c in pending)
                c.InFlight = false;

            // The cache was wiped while this response was in flight; applying it would resurrect
            // resources the purge was meant to remove.
            if (generation != _generation)
                return;

            ApplyTempIds(response.TempIdMapping);
            ProcessCommandResults(response.SyncStatus, pending);
            ApplyServerChanges(response);
        }
    }

    // ---- Completed tasks (on demand, never persisted) --------------------------------------------

    /// <summary>Pages a completed-items fetch takes before giving up on an unbounded history.</summary>
    private const int MaxCompletedPages = 10;

    /// <summary>What a completed-items fetch brought back.</summary>
    /// <param name="Count">Completed tasks now held.</param>
    /// <param name="Truncated">
    /// True when the account has more than the fetch was willing to page through, so the caller can
    /// say the list is the most recent rather than all of them.
    /// </param>
    public sealed record CompletedFetch(int Count, bool Truncated);

    /// <summary>
    /// Fetches recently completed tasks across the account. Results are held in memory only — they
    /// are never written to the snapshot, because nothing would ever arrive to correct or remove them.
    /// </summary>
    /// <remarks>
    /// Deliberately not narrowed to the view being looked at, even though the endpoint would take a
    /// project: the same fetch then serves every view, including the label, filter and smart views
    /// the endpoint has no way to express. Which of them are shown is decided locally, by the same
    /// predicate that decides it for active tasks.
    /// </remarks>
    public async Task<CompletedFetch> FetchCompletedAsync(CancellationToken ct = default)
    {
        string token;
        int generation;
        lock (_gate)
        {
            token = _secrets.GetToken()
                    ?? throw new InvalidOperationException("No Todoist token is stored.");
            generation = _generation;
        }

        var until = _clock.UtcNow;
        var since = until - CompletedQuery.MaxWindow;

        var fetched = new List<ResourceChange>();
        string? cursor = null;
        var truncated = false;

        for (var page = 0; page < MaxCompletedPages; page++)
        {
            CompletedPage result;
            try
            {
                result = await _api.GetCompletedAsync(token, new CompletedQuery(since, until, cursor), ct);
            }
            catch (TodoistAuthException)
            {
                lock (_gate)
                {
                    _secrets.ClearToken();
                    PurgeLocal();
                }
                throw;
            }

            fetched.AddRange(result.Items);
            cursor = result.NextCursor;

            if (cursor is null)
                break;

            // The last page allowed still reported more behind it.
            if (page == MaxCompletedPages - 1)
                truncated = true;
        }

        lock (_gate)
        {
            // Wiped while this was in flight: these belong to an account we no longer hold.
            if (generation != _generation)
                return new CompletedFetch(0, false);

            _completed.Clear();
            foreach (var change in fetched)
                _completed[change.Id] = change.Json;

            return new CompletedFetch(CompletedItems().Count, truncated);
        }
    }

    /// <summary>Drops the fetched completed tasks, leaving only what the model itself holds.</summary>
    public void ClearCompleted()
    {
        lock (_gate)
            _completed.Clear();
    }

    /// <summary>
    /// Drops a deleted task from the completed fetch. A tombstone arrives once, and the fetch is the
    /// only copy that would otherwise keep showing it.
    /// </summary>
    private void Forget(string type, string id)
    {
        if (type == ResourceType.Items)
            _completed.Remove(id);
    }

    // ---- Optimistic writes (field-level) ---------------------------------------------------------

    /// <summary>
    /// Creates a task from raw text using the server's natural-language parsing, so capture matches
    /// the web app exactly, and folds the created task into the model.
    /// </summary>
    /// <returns><c>false</c> if Todoist was unreachable, so the caller can fall back to local parsing.</returns>
    public async Task<bool> QuickAddOnlineAsync(string text, CancellationToken ct = default)
    {
        string token;
        int generation;
        lock (_gate)
        {
            token = _secrets.GetToken()
                    ?? throw new InvalidOperationException("No Todoist token is stored.");
            generation = _generation;
        }

        ResourceChange created;
        try
        {
            created = await _api.QuickAddAsync(token, text, ct);
        }
        catch (TodoistNetworkException)
        {
            return false;
        }
        catch (TodoistAuthException)
        {
            lock (_gate)
            {
                _secrets.ClearToken();
                PurgeLocal();
            }
            throw;
        }

        lock (_gate)
        {
            if (generation != _generation)
                return true; // created server-side, but our cache was wiped meanwhile

            var json = created.Json.DeepClone().AsObject();
            _store.PutResource(created.ResourceType, created.Id, json.ToJsonString());
            Model.Upsert(created.ResourceType, created.Id, json);
        }
        return true;
    }

    /// <summary>Creates a task optimistically and queues an <c>item_add</c>. Returns the temp id.</summary>
    public string AddItem(JsonObject fields)
    {
        lock (_gate)
        {
            var tempId = "t-" + Guid.NewGuid().ToString("N");
            var args = fields.DeepClone().AsObject();
            args.Remove("id"); // the server assigns the id; temp_id is our handle until it does

            var obj = args.DeepClone().AsObject();
            obj["id"] = tempId;

            Persist("item_add", args, tempId, null, [new StoredResource(ResourceType.Items, tempId, obj.ToJsonString())], []);
            Model.Upsert(ResourceType.Items, tempId, obj);
            return tempId;
        }
    }

    /// <summary>Applies changed fields to a task optimistically and queues an <c>item_update</c>.</summary>
    public void UpdateItem(string id, JsonObject changes)
    {
        lock (_gate)
        {
            // Nothing held locally means the task is already gone; sending the edit anyway would
            // queue a command that can only fail until it poisons.
            if (Model.Get(ResourceType.Items, id) is not { } existing)
                return;

            var prior = existing.ToJsonString();
            var updated = existing.DeepClone().AsObject();
            foreach (var kv in changes)
                updated[kv.Key] = kv.Value?.DeepClone();

            var args = changes.DeepClone().AsObject();
            args["id"] = id;

            Persist("item_update", args, null, prior, [new StoredResource(ResourceType.Items, id, updated.ToJsonString())], []);
            Model.Upsert(ResourceType.Items, id, updated);
        }
    }

    /// <summary>Completes a task via <c>item_close</c>, which advances a recurring task instead.</summary>
    /// <remarks>
    /// An ordinary task is ticked off locally straight away. A recurring one is left exactly as it
    /// is: the server moves it to its next occurrence rather than finishing it, and working out
    /// where that lands is the recurrence guessing that belongs on the server.
    /// </remarks>
    public void CompleteItem(string id)
    {
        lock (_gate)
        {
            var existing = Model.Get(ResourceType.Items, id);
            var recurring = existing is not null && Projections.ToTaskItem(existing).IsRecurring;

            // Nothing on the row changes for a recurring close, so pressing the key again is the
            // natural response to a press that looks like it did nothing — and every close the
            // server takes advances the schedule another occurrence. An ordinary task leaves the
            // list when it's closed, so there is nothing there to press twice, and guarding it too
            // would swallow a genuine re-close after an undo.
            if (recurring && HasPendingClose(id))
                return;

            JsonObject? completed = null;
            string? prior = null;
            StoredResource[] upserts = [];

            if (existing is not null)
            {
                prior = existing.ToJsonString();

                if (!recurring)
                {
                    completed = existing.DeepClone().AsObject();
                    completed["checked"] = true;
                    upserts = [new StoredResource(ResourceType.Items, id, completed.ToJsonString())];
                }
            }

            // A recurring close mutates nothing locally, so it records no prior: a prior means
            // "this command owns the resource until it lands", and owning a task it never touched
            // would drop the server's advanced occurrence when the close isn't acked that round.
            var cmd = Persist("item_close", new JsonObject { ["id"] = id }, null, recurring ? null : prior, upserts, []);

            if (completed is not null)
                Model.Upsert(ResourceType.Items, id, completed);

            if (existing is null)
                return;

            // Reversible only while it's queued. Once the server has advanced the schedule there is
            // no putting the occurrence back, and item_uncomplete would reopen a task that was
            // never closed — so Ctrl+Z stops here rather than reporting a success it didn't manage.
            if (recurring)
                RecordUndoBarrier(cmd);
            else
                RecordUndoable(cmd, id, prior);
        }
    }

    /// <summary>Whether a close for this task is already queued and unsent.</summary>
    private bool HasPendingClose(string id)
        => _outbox.Any(c => c is { State: OutboxState.Pending, Type: "item_close" }
                            && ParseArgs(c)["id"] is JsonValue v && v.ToString() == id);

    /// <summary>
    /// Sets a task's due date from words for the server to resolve, and queues an
    /// <c>item_update</c>. Only the string goes on the wire, but the local copy keeps the date and
    /// the recurrence flag it already had: <c>due</c> is one object, and replacing it wholesale
    /// takes the task out of every date view and makes a recurring one look ordinary to the next
    /// close.
    /// </summary>
    /// <param name="recurring">
    /// Whether the words describe a repeat, as best the caller can tell. The server has the final
    /// say, but until it answers this is what the next close goes on — a task just made repeating
    /// would otherwise be ticked off, and one just taken off a repeat would be advanced.
    /// </param>
    public void SetItemDueString(string id, string text, bool recurring)
    {
        lock (_gate)
        {
            if (Model.Get(ResourceType.Items, id) is not { } existing)
                return;

            var trimmed = text.Trim();
            var prior = existing.ToJsonString();
            var updated = existing.DeepClone().AsObject();

            // Set either way rather than only when true: a task moved off a repeat and onto a plain
            // schedule has stopped repeating, and leaving the old flag standing would have the next
            // close advance a task the server is about to finish.
            var due = existing["due"] is JsonObject held ? held.DeepClone().AsObject() : [];
            due["string"] = trimmed;
            due["is_recurring"] = recurring;
            updated["due"] = due;

            var args = new JsonObject { ["id"] = id, ["due"] = ItemFields.DueString(trimmed) };

            Persist("item_update", args, null, prior, [new StoredResource(ResourceType.Items, id, updated.ToJsonString())], []);
            Model.Upsert(ResourceType.Items, id, updated);
        }
    }

    /// <summary>Reopens a completed task and queues an <c>item_uncomplete</c>.</summary>
    /// <remarks>
    /// The task may only exist in the on-demand completed fetch, which is where a long-completed one
    /// comes from. Reopening it makes it an ordinary active task, so it moves into the model — and
    /// into the snapshot with it, since from here on incremental sync will keep it current.
    /// </remarks>
    public void ReopenItem(string id)
    {
        lock (_gate)
        {
            var existing = Model.Get(ResourceType.Items, id) ?? _completed.GetValueOrDefault(id);
            JsonObject? reopened = null;

            // Only what the model already held counts as prior state. A fetched task had none, and
            // recording one would have undo write a completed task into the snapshot.
            var prior = Model.Get(ResourceType.Items, id)?.ToJsonString();
            StoredResource[] upserts = [];

            if (existing is not null)
            {
                reopened = existing.DeepClone().AsObject();
                reopened["checked"] = false;
                upserts = [new StoredResource(ResourceType.Items, id, reopened.ToJsonString())];
            }

            Persist("item_uncomplete", new JsonObject { ["id"] = id }, null, prior, upserts, []);
            if (reopened is not null)
                Model.Upsert(ResourceType.Items, id, reopened);

            // It is active now, so the completed list has no further claim on it.
            _completed.Remove(id);
        }
    }

    /// <summary>Deletes a task optimistically and queues an <c>item_delete</c>.</summary>
    public void DeleteItem(string id)
    {
        lock (_gate)
        {
            var existing = Model.Get(ResourceType.Items, id);
            var prior = existing?.ToJsonString();
            ResourceKey[] deletes = existing is not null ? [new ResourceKey(ResourceType.Items, id)] : [];

            var cmd = Persist("item_delete", new JsonObject { ["id"] = id }, null, prior, [], deletes);
            if (existing is not null)
            {
                Model.Remove(ResourceType.Items, id);
                RecordUndoable(cmd, id, prior);
            }
        }
    }

    /// <summary>
    /// Moves a task one place up or down among its siblings — the tasks sharing its project and
    /// parent — and queues an <c>item_reorder</c>. Ordering is computed over the whole sibling set,
    /// never over a filtered view, so hidden tasks keep their positions.
    /// </summary>
    /// <returns>False when the task is already at that end, or isn't held.</returns>
    public bool MoveItem(string id, int offset)
    {
        lock (_gate)
        {
            if (Model.Get(ResourceType.Items, id) is not { } json)
                return false;

            var item = Projections.ToTaskItem(json);
            var siblings = SiblingsOf(item);

            var from = siblings.FindIndex(i => i.Id == id);
            if (from < 0)
                return false;

            // Step over completed siblings: they aren't on screen, so swapping with one would look
            // like the keypress did nothing.
            var step = Math.Sign(offset);
            var to = from;
            for (var moved = 0; moved < Math.Abs(offset); moved++)
            {
                do
                {
                    to += step;
                }
                while (to >= 0 && to < siblings.Count && siblings[to].Completed);

                if (to < 0 || to >= siblings.Count)
                    return false;
            }

            var ids = siblings.Select(i => i.Id).ToList();
            ids.RemoveAt(from);
            ids.Insert(to, id);
            ReorderLocked(ids);
            return true;
        }
    }

    /// <summary>
    /// Makes a task a child of the sibling above it, and queues an <c>item_move</c>. Todoist has no
    /// notion of indent beyond parentage, so this is a re-parent.
    /// </summary>
    /// <returns>False when there is no sibling above to adopt it.</returns>
    public bool IndentItem(string id)
    {
        lock (_gate)
        {
            if (Model.Get(ResourceType.Items, id) is not { } json)
                return false;

            var item = Projections.ToTaskItem(json);
            var siblings = SiblingsOf(item);
            var index = siblings.FindIndex(i => i.Id == id);
            if (index <= 0)
                return false;

            // Adopting a completed task would be invisible: it isn't in the outline, so the row
            // wouldn't appear to move at all.
            var adopter = siblings.Take(index).LastOrDefault(i => !i.Completed);
            return adopter is not null && MoveTo(id, parentId: adopter.Id);
        }
    }

    /// <summary>Promotes a sub-task to sit alongside its parent, and queues an <c>item_move</c>.</summary>
    /// <returns>False when the task is already top level.</returns>
    public bool OutdentItem(string id)
    {
        lock (_gate)
        {
            if (Model.Get(ResourceType.Items, id) is not { } json)
                return false;

            var item = Projections.ToTaskItem(json);
            if (item.ParentId is not { } parentId)
                return false;

            var parent = Model.Get(ResourceType.Items, parentId) is { } parentJson
                ? Projections.ToTaskItem(parentJson)
                : null;

            // Alongside the parent means the parent's own place: under its parent if it has one,
            // otherwise its section — moving to the project would evict the task from that section.
            if (parent?.ParentId is { } grandparent)
                return MoveTo(id, parentId: grandparent);

            // A section the model hasn't seen can't be moved into, and there is no longer anything
            // to be evicted from, so the project below is the right place to land.
            if (parent?.SectionId is { } section && Model.Sections().Any(s => s.Id == section))
                return MoveTo(id, sectionId: section);

            return (parent?.ProjectId ?? item.ProjectId) is { } project && MoveTo(id, projectId: project);
        }
    }

    /// <summary>Moves a task to another project, keeping it top level there.</summary>
    public bool MoveItemToProject(string id, string projectId)
    {
        lock (_gate)
            return MoveTo(id, projectId: projectId);
    }

    /// <summary>
    /// Re-homes a task and queues an <c>item_move</c>. Todoist takes exactly one destination, and
    /// in each case puts the task last where it lands — the local copy is updated to match, so the
    /// row doesn't sit somewhere it won't stay.
    /// </summary>
    private bool MoveTo(string id, string? parentId = null, string? sectionId = null, string? projectId = null)
    {
        if (Model.Get(ResourceType.Items, id) is not { } existing)
            return false;

        var prior = existing.ToJsonString();
        var moved = existing.DeepClone().AsObject();
        var args = new JsonObject { ["id"] = id };

        if (parentId is not null)
        {
            if (Model.Get(ResourceType.Items, parentId) is not { } parentJson)
                return false;

            // A sub-task lives wherever its parent lives.
            var parent = Projections.ToTaskItem(parentJson);
            args["parent_id"] = parentId;
            moved["parent_id"] = parentId;
            moved["project_id"] = parent.ProjectId;
            moved["section_id"] = parent.SectionId;
        }
        else if (sectionId is not null)
        {
            // The destination project is read off the section, so an unknown section would file the
            // task under no project at all — out of every view, and counted against the wrong
            // siblings when ordering it.
            if (Model.Sections().FirstOrDefault(s => s.Id == sectionId) is not { } section)
                return false;

            args["section_id"] = sectionId;
            moved["parent_id"] = null;
            moved["section_id"] = sectionId;
            moved["project_id"] = section.ProjectId;
        }
        else if (projectId is not null)
        {
            args["project_id"] = projectId;
            moved["parent_id"] = null;
            moved["section_id"] = null; // the server drops the section when moving to a project
            moved["project_id"] = projectId;
        }
        else
        {
            return false;
        }

        moved["child_order"] = NextOrderAt(Projections.ToTaskItem(moved), id);

        Persist("item_move", args, null, prior, [new StoredResource(ResourceType.Items, id, moved.ToJsonString())], []);
        Model.Upsert(ResourceType.Items, id, moved);
        return true;
    }

    /// <summary>The position a task takes when the server files it last among its new siblings.</summary>
    private int NextOrderAt(TaskItem destination, string movingId)
    {
        var siblings = Model.Items()
            .Where(i => i.Id != movingId && i.ProjectId == destination.ProjectId && i.ParentId == destination.ParentId)
            .ToList();

        return siblings.Count == 0 ? 1 : siblings.Max(i => i.ChildOrder) + 1;
    }

    private List<TaskItem> SiblingsOf(TaskItem item)
        => Model.Items()
            .Where(i => i.ProjectId == item.ProjectId && i.ParentId == item.ParentId)
            .OrderBy(i => i.ChildOrder)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>Creates a project optimistically and queues a <c>project_add</c>.</summary>
    public string AddProject(string name, string? parentId = null)
    {
        lock (_gate)
        {
            var tempId = "t-" + Guid.NewGuid().ToString("N");
            var args = new JsonObject { ["name"] = name };
            if (parentId is not null)
                args["parent_id"] = parentId;

            var obj = args.DeepClone().AsObject();
            obj["id"] = tempId;

            Persist("project_add", args, tempId, null, [new StoredResource(ResourceType.Projects, tempId, obj.ToJsonString())], []);
            Model.Upsert(ResourceType.Projects, tempId, obj);
            return tempId;
        }
    }

    public void RenameProject(string id, string name)
        => UpdateResource(ResourceType.Projects, "project_update", id, new JsonObject { ["name"] = name });

    public void SetProjectFavorite(string id, bool favorite)
        => UpdateResource(ResourceType.Projects, "project_update", id, new JsonObject { ["is_favorite"] = favorite });

    /// <summary>
    /// Deletes a project and everything filed under it — sub-projects included, as the server does —
    /// and queues a <c>project_delete</c>.
    /// </summary>
    public void DeleteProject(string id)
    {
        lock (_gate)
        {
            if (Model.Get(ResourceType.Projects, id) is null)
                return;

            // Descendants too, or the children survive locally as projects nothing can reach.
            var doomed = ProjectTree.WithDescendants(Model.Projects(), [id]);

            var deletes = new List<ResourceKey>();
            foreach (var project in doomed)
                deletes.Add(new ResourceKey(ResourceType.Projects, project));
            deletes.AddRange(Model.Sections().Where(s => s.ProjectId is { } p && doomed.Contains(p)).Select(s => new ResourceKey(ResourceType.Sections, s.Id)));
            deletes.AddRange(Model.Items().Where(i => i.ProjectId is { } p && doomed.Contains(p)).Select(i => new ResourceKey(ResourceType.Items, i.Id)));

            RemoveAll("project_delete", id, deletes);
        }
    }

    /// <summary>Creates a section within a project and queues a <c>section_add</c>.</summary>
    public string AddSection(string name, string projectId)
    {
        lock (_gate)
        {
            var tempId = "t-" + Guid.NewGuid().ToString("N");
            var args = new JsonObject { ["name"] = name, ["project_id"] = projectId };

            var obj = args.DeepClone().AsObject();
            obj["id"] = tempId;

            Persist("section_add", args, tempId, null, [new StoredResource(ResourceType.Sections, tempId, obj.ToJsonString())], []);
            Model.Upsert(ResourceType.Sections, tempId, obj);
            return tempId;
        }
    }

    public void RenameSection(string id, string name)
        => UpdateResource(ResourceType.Sections, "section_update", id, new JsonObject { ["name"] = name });

    /// <summary>Deletes a section and the tasks in it, and queues a <c>section_delete</c>.</summary>
    public void DeleteSection(string id)
    {
        lock (_gate)
        {
            if (Model.Get(ResourceType.Sections, id) is null)
                return;

            var deletes = new List<ResourceKey> { new(ResourceType.Sections, id) };
            deletes.AddRange(Model.Items().Where(i => i.SectionId == id).Select(i => new ResourceKey(ResourceType.Items, i.Id)));

            RemoveAll("section_delete", id, deletes);
        }
    }

    // ---- Reminders ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds a reminder a set number of minutes before the task falls due, and queues a
    /// <c>reminder_add</c>.
    /// </summary>
    public string? AddRelativeReminder(string itemId, int minutesBefore)
        => AddReminder(itemId, new JsonObject
        {
            ["item_id"] = itemId,
            ["type"] = "relative",
            ["minute_offset"] = minutesBefore,
        });

    /// <summary>Adds a reminder for a moment of its own, and queues a <c>reminder_add</c>.</summary>
    public string? AddAbsoluteReminder(string itemId, DateOnly date, TimeOnly time)
        => AddReminder(itemId, new JsonObject
        {
            ["item_id"] = itemId,
            ["type"] = "absolute",
            ["due"] = ItemFields.Due(date, time),
        });

    /// <returns>The new reminder's temporary id, or null when the task isn't one we hold.</returns>
    private string? AddReminder(string itemId, JsonObject args)
    {
        lock (_gate)
        {
            // A reminder on a task we don't have can only fail until it poisons the outbox.
            if (Model.Get(ResourceType.Items, itemId) is null)
                return null;

            var tempId = "t-" + Guid.NewGuid().ToString("N");
            var obj = args.DeepClone().AsObject();
            obj["id"] = tempId;

            Persist("reminder_add", args, tempId, null, [new StoredResource(ResourceType.Reminders, tempId, obj.ToJsonString())], []);
            Model.Upsert(ResourceType.Reminders, tempId, obj);
            return tempId;
        }
    }

    public void DeleteReminder(string id)
    {
        lock (_gate)
        {
            if (Model.Get(ResourceType.Reminders, id) is null)
                return;

            // Through the shared path so the prior takes the form Load recognises as a barrier.
            // Recording one here alone would only hold until the app restarted.
            RemoveAll("reminder_delete", id, [new ResourceKey(ResourceType.Reminders, id)]);
        }
    }

    // ---- Labels ------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces the labels on a task and queues an <c>item_update</c>. Tasks carry labels by name,
    /// so this is the whole set rather than a delta — Todoist has no add-one-label command.
    /// </summary>
    public void SetItemLabels(string id, IReadOnlyList<string> labels)
    {
        var array = new JsonArray();
        foreach (var label in labels.Distinct(StringComparer.OrdinalIgnoreCase))
            array.Add(label);

        UpdateItem(id, new JsonObject { ["labels"] = array });
    }

    /// <summary>Creates a label optimistically and queues a <c>label_add</c>.</summary>
    public string AddLabel(string name)
    {
        lock (_gate)
        {
            var tempId = "t-" + Guid.NewGuid().ToString("N");
            var args = new JsonObject { ["name"] = name };

            var obj = args.DeepClone().AsObject();
            obj["id"] = tempId;

            Persist("label_add", args, tempId, null, [new StoredResource(ResourceType.Labels, tempId, obj.ToJsonString())], []);
            Model.Upsert(ResourceType.Labels, tempId, obj);
            return tempId;
        }
    }

    /// <summary>
    /// Renames a label and queues a <c>label_update</c>. The tasks wearing it are left alone here:
    /// they hold the label by name, and only the server knows whether the rename carried across to
    /// them. Whatever it reports on the next sync is the truth, and inventing it locally would risk
    /// showing labels that don't exist on the account.
    /// </summary>
    public void RenameLabel(string id, string name)
        => UpdateResource(ResourceType.Labels, "label_update", id, new JsonObject { ["name"] = name });

    public void SetLabelFavorite(string id, bool favorite)
        => UpdateResource(ResourceType.Labels, "label_update", id, new JsonObject { ["is_favorite"] = favorite });

    /// <summary>
    /// Deletes a label and queues a <c>label_delete</c>. Todoist takes the label off every task as
    /// well, so the tasks that wore it are updated locally to match — otherwise they would keep
    /// showing a label the account no longer has.
    /// </summary>
    public void DeleteLabel(string id)
    {
        lock (_gate)
        {
            if (Model.Get(ResourceType.Labels, id) is not { } label)
                return;

            var name = Projections.ToLabel(label).Name;
            var priors = new JsonArray
            {
                new JsonObject { ["type"] = ResourceType.Labels, ["resource"] = label.DeepClone() },
            };

            // The tasks change too, so their prior state belongs on the command: reverting has to
            // put the label back on them, not just recreate the label itself.
            var upserts = new List<StoredResource>();
            foreach (var raw in Model.All(ResourceType.Items).ToList())
            {
                var item = Projections.ToTaskItem(raw);
                if (!item.Labels.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                priors.Add(new JsonObject { ["type"] = ResourceType.Items, ["resource"] = raw.DeepClone() });

                var stripped = raw.DeepClone().AsObject();
                var kept = new JsonArray();
                foreach (var remaining in item.Labels.Where(l => !string.Equals(l, name, StringComparison.OrdinalIgnoreCase)))
                    kept.Add(remaining);
                stripped["labels"] = kept;

                upserts.Add(new StoredResource(ResourceType.Items, item.Id, stripped.ToJsonString()));
            }

            // cascade "all" is Todoist's default; sending it makes the intent explicit in the outbox.
            var args = new JsonObject { ["id"] = id, ["cascade"] = "all" };
            var cmd = Persist("label_delete", args, null, priors.ToJsonString(), upserts, [new ResourceKey(ResourceType.Labels, id)]);

            foreach (var upsert in upserts)
                Model.Upsert(upsert.Type, upsert.Id, JsonNode.Parse(upsert.Json)!.AsObject());
            Model.Remove(ResourceType.Labels, id);

            // Reversible only while it's still queued. Once the server has it there is no undelete,
            // so Ctrl+Z stops here rather than reaching past to something else.
            RecordUndoBarrier(cmd);
        }
    }

    /// <summary>
    /// Queues a delete that removes several resources at once, keeping every one of them as the
    /// command's prior state so reverting restores the contents and not just the container.
    /// </summary>
    private void RemoveAll(string commandType, string id, IReadOnlyList<ResourceKey> deletes)
    {
        var priors = new JsonArray();
        foreach (var key in deletes)
        {
            if (Model.Get(key.Type, key.Id) is { } resource)
                priors.Add(new JsonObject { ["type"] = key.Type, ["resource"] = resource.DeepClone() });
        }

        var cmd = Persist(commandType, new JsonObject { ["id"] = id }, null, priors.ToJsonString(), [], deletes);
        foreach (var key in deletes)
            Model.Remove(key.Type, key.Id);

        // Destructive, and not something Undo can reverse once the server has it: Todoist has no
        // undelete. Mark the point so Ctrl+Z reports that rather than reaching past it.
        RecordUndoBarrier(cmd);
    }

    /// <summary>Applies changed fields to a non-task resource and queues its update command.</summary>
    private void UpdateResource(string type, string commandType, string id, JsonObject changes)
    {
        lock (_gate)
        {
            if (Model.Get(type, id) is not { } existing)
                return;

            var prior = existing.ToJsonString();
            var updated = existing.DeepClone().AsObject();
            foreach (var kv in changes)
                updated[kv.Key] = kv.Value?.DeepClone();

            var args = changes.DeepClone().AsObject();
            args["id"] = id;

            Persist(commandType, args, null, prior, [new StoredResource(type, id, updated.ToJsonString())], []);
            Model.Upsert(type, id, updated);
        }
    }

    /// <summary>Applies a new order to the given tasks, numbering them consecutively.</summary>
    public void ReorderItems(IReadOnlyList<string> orderedIds)
    {
        lock (_gate)
            ReorderLocked(orderedIds);
    }

    private void ReorderLocked(IReadOnlyList<string> orderedIds)
    {
        var entries = new JsonArray();
        var upserts = new List<StoredResource>();
        var updated = new List<(string Id, JsonObject Json)>();
        var priors = new JsonArray();

        var order = 0;
        foreach (var id in orderedIds.Distinct(StringComparer.Ordinal))
        {
            // Skip ids we no longer hold: sending them would fail forever in the outbox.
            if (Model.Get(ResourceType.Items, id) is not { } existing)
                continue;

            order++;

            // Only the tasks whose position actually changed need sending. Without this a one-place
            // move rewrites and re-persists every sibling in the project.
            if (JsonRead.Int(existing, "child_order") == order)
                continue;

            entries.Add(new JsonObject { ["id"] = id, ["child_order"] = order });
            priors.Add(existing.DeepClone());

            var clone = existing.DeepClone().AsObject();
            clone["child_order"] = order;
            upserts.Add(new StoredResource(ResourceType.Items, id, clone.ToJsonString()));
            updated.Add((id, clone));
        }

        if (entries.Count == 0)
            return;

        Persist("item_reorder", new JsonObject { ["items"] = entries }, null, priors.ToJsonString(), upserts, []);
        foreach (var (id, json) in updated)
            Model.Upsert(ResourceType.Items, id, json);
    }

    /// <summary>
    /// Reverses the most recent completion or deletion. If its command hasn't been sent it is simply
    /// dropped and the resource restored; if it has, the opposite command is issued.
    /// </summary>
    /// <returns><c>false</c> when there is nothing left to undo, or the task it applied to is gone.</returns>
    public bool Undo()
    {
        lock (_gate)
        {
            // Keep going past entries that can no longer be reversed — the task was deleted on
            // another device, say — rather than reporting "nothing to undo" with history left.
            while (_undoStack.Count > 0)
            {
                var uuid = _undoStack[^1];
                _undoStack.RemoveAt(_undoStack.Count - 1);
                var record = _undoable.GetValueOrDefault(uuid);
                _undoable.Remove(uuid);

                var queued = _outbox.FirstOrDefault(c => c.Uuid == uuid);
                if (queued is { InFlight: false })
                {
                    RevertLocked(queued);
                    return true;
                }

                if (record is null)
                    continue;

                // A destructive write we can't reverse. Consume it and stop, rather than reaching
                // past it and silently undoing something the user wasn't thinking about.
                if (record.Type == UndoBarrier)
                    return false;

                if (record.Type == "item_close")
                {
                    // Already applied server-side; only meaningful if the task is still there.
                    if (Model.Get(ResourceType.Items, record.Id) is null)
                        continue;
                    ReopenItem(record.Id);
                    return true;
                }

                // Todoist cannot undelete, so the task is recreated from the state we last held.
                if (record.PriorJson is { } json && TryParse(json) is { } prior)
                {
                    var fields = ItemFields.ForRecreate(prior);
                    if (fields["content"] is null)
                        continue;
                    AddItem(fields);
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Drops a queued command and restores the resource to the last state the server gave us, so
    /// the local copy returns to server truth rather than keeping a half-applied edit.
    /// </summary>
    /// <remarks>Does nothing once the command is on the wire: the server is applying it regardless.</remarks>
    public void Revert(string uuid)
    {
        lock (_gate)
        {
            if (_outbox.FirstOrDefault(c => c.Uuid == uuid) is { InFlight: false } cmd)
                RevertLocked(cmd);
        }
    }

    private void RevertLocked(OutboxCommand cmd)
    {
        if (IsCreate(cmd))
        {
            if (cmd.TempId is { } temp)
                RemoveObject(temp);
        }
        else
        {
            RestorePriors(cmd);
        }

        _outbox.Remove(cmd);
        _store.DeleteCommands([cmd.Uuid]);
        ForgetUndoable(cmd.Uuid);
    }

    /// <summary>
    /// Puts every resource a command touched back to the state the server last gave us, so the
    /// local copy returns to server truth instead of keeping a write that isn't going to happen.
    /// </summary>
    private void RestorePriors(OutboxCommand cmd)
    {
        if (cmd.PriorJson is not { } prior || TryParseNode(prior) is not { } node)
            return;

        foreach (var restored in node is JsonArray array ? array.OfType<JsonNode>() : [node])
        {
            if (restored is not JsonObject entry)
                continue;

            // A cascading delete records what type each removed resource was, since one command
            // can span projects, sections and tasks.
            var (type, resource) = entry["resource"] is JsonObject inner
                ? (entry["type"]?.ToString() ?? ResourceTypeFor(cmd), inner)
                : (ResourceTypeFor(cmd), entry);

            if (resource["id"] is not JsonValue idValue)
                continue;

            var id = idValue.ToString();
            var copy = resource.DeepClone().AsObject();
            _store.PutResource(type, id, copy.ToJsonString());
            Model.Upsert(type, id, copy);
        }
    }

    // ---- Reconciliation --------------------------------------------------------------------------

    private void ApplyTempIds(IReadOnlyDictionary<string, string> mapping)
    {
        foreach (var (temp, real) in mapping)
        {
            if (Model.Find(temp) is { } found)
            {
                Model.Rename(found.Type, temp, real);
                _store.RenameResource(found.Type, temp, real);
            }

            foreach (var (type, id, obj) in Model.RewriteReferences(temp, real).ToList())
                _store.PutResource(type, id, obj.ToJsonString());

            foreach (var cmd in _outbox)
            {
                var args = ParseArgs(cmd);
                var changed = false;

                foreach (var key in IdKeys)
                {
                    if (args.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.ToString() == temp)
                    {
                        args[key] = real;
                        changed = true;
                    }
                }

                // A reorder holds its ids in an array; missing these would send the server a temp id.
                if (args["items"] is JsonArray items)
                {
                    foreach (var entry in items)
                    {
                        if (entry is JsonObject o && o["id"] is JsonValue entryId && entryId.ToString() == temp)
                        {
                            o["id"] = real;
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    cmd.ArgsJson = args.ToJsonString();
                    _store.UpdateCommand(cmd);
                }
            }

            // Undo records hold the id as it was at write time; without this an undo after the
            // server assigns the real id targets a temp id that no longer exists anywhere.
            foreach (var uuid in _undoable.Keys.ToList())
            {
                if (_undoable[uuid].Id == temp)
                    _undoable[uuid] = _undoable[uuid] with { Id = real };
            }
        }
    }

    private void ProcessCommandResults(IReadOnlyDictionary<string, CommandResult> syncStatus, List<OutboxCommand> sent)
    {
        foreach (var cmd in sent)
        {
            if (!_outbox.Contains(cmd))
                continue; // already cancelled by an earlier cascade this round

            if (!syncStatus.TryGetValue(cmd.Uuid, out var result))
            {
                // No verdict: keep it pending, but count the rounds so a command the server never
                // reports on cannot block its resource forever.
                cmd.NoVerdictRounds++;
                if (cmd.NoVerdictRounds >= _attemptCeiling)
                    Fail(cmd, "Todoist did not report a result for this change.");
                else
                    _store.UpdateCommand(cmd);
                continue;
            }

            cmd.NoVerdictRounds = 0;

            if (result.Ok)
            {
                RemoveCommand(cmd);
                continue;
            }

            cmd.Attempts++;
            cmd.LastError = result.Error ?? result.ErrorCode;

            if (IsCreate(cmd))
            {
                CascadeCancel(cmd);
                continue;
            }

            if (cmd.Attempts >= _attemptCeiling)
                Fail(cmd, cmd.LastError);
            else
                _store.UpdateCommand(cmd);
        }
    }

    /// <summary>
    /// Gives up on a command and rolls the local copy back to what the server last told us.
    /// </summary>
    /// <remarks>
    /// The write is never going to land, and nothing else would undo it: the server has no reason to
    /// resend a resource that never changed there, and the sync token has long since moved past it.
    /// A delete is the one that hurts — the resource stays gone locally while the account still has
    /// it, and a label quietly missing from a dozen tasks is not something anyone would spot.
    /// The command stays in the outbox, failed, so the count still tells the user something went
    /// wrong. Creates are left alone: an <c>_add</c> the server never ruled on may well have been
    /// applied, and dropping the local copy would throw away what the user typed.
    /// </remarks>
    private void Fail(OutboxCommand cmd, string? error)
    {
        cmd.State = OutboxState.Failed;
        cmd.LastError = error;

        if (!IsCreate(cmd))
            RestorePriors(cmd);

        _store.UpdateCommand(cmd);

        // Nothing left to reverse, and the barrier it may have recorded should no longer stop Undo.
        ForgetUndoable(cmd.Uuid);
    }

    private void ApplyServerChanges(SyncResponse response)
    {
        var pendingKeys = PendingResourceKeys();
        var reorderedKeys = PendingReorderKeys();
        var upserts = new List<StoredResource>();
        var deletes = new List<ResourceKey>();

        foreach (var change in response.Changes)
        {
            var key = new ResourceKey(change.ResourceType, change.Id);
            var held = pendingKeys.Contains(key) || reorderedKeys.Contains(key);

            // Hold the deletion until the local write that covers this resource resolves; a queued
            // command must not be left naming a task the server has already removed.
            if (change.IsDeleted && held)
            {
                if (!_deferredDeletes.Contains(key))
                    _deferredDeletes.Add(key);
                continue;
            }

            // An un-acked edit of our own owns this resource until it lands.
            if (pendingKeys.Contains(key))
                continue;

            if (change.IsDeleted)
            {
                Forget(change.ResourceType, change.Id);
                if (Model.Remove(change.ResourceType, change.Id))
                    deletes.Add(new ResourceKey(change.ResourceType, change.Id));
            }
            else
            {
                var clone = change.Json.DeepClone().AsObject();

                // A pending reorder only owns the position, so take everything else the server sends
                // rather than dropping the change — the token advances past it either way.
                if (reorderedKeys.Contains(key)
                    && Model.Get(change.ResourceType, change.Id)?["child_order"] is { } localOrder)
                {
                    clone["child_order"] = localOrder.DeepClone();
                }

                Model.Upsert(change.ResourceType, change.Id, clone);
                upserts.Add(new StoredResource(change.ResourceType, change.Id, clone.ToJsonString()));
            }
        }

        // A full sync is the whole live set and carries no tombstones, so anything held locally
        // that it doesn't mention is gone — and this response is the only chance to notice, because
        // the token moves past those deletions either way. Resources a queued command owns are left
        // alone: a create the server hasn't seen yet is missing for a reason of our own making.
        if (response.FullSync)
        {
            var live = response.Changes.Select(c => new ResourceKey(c.ResourceType, c.Id)).ToHashSet();

            foreach (var type in ResourceType.All)
            {
                foreach (var id in Model.Keys(type))
                {
                    var stale = new ResourceKey(type, id);
                    if (live.Contains(stale) || pendingKeys.Contains(stale) || reorderedKeys.Contains(stale))
                        continue;

                    if (Model.Remove(type, id))
                        deletes.Add(stale);
                }
            }
        }

        for (var i = _deferredDeletes.Count - 1; i >= 0; i--)
        {
            var deferred = _deferredDeletes[i];
            if (pendingKeys.Contains(deferred) || reorderedKeys.Contains(deferred))
                continue;
            _deferredDeletes.RemoveAt(i);
            Forget(deferred.Type, deferred.Id);
            if (Model.Remove(deferred.Type, deferred.Id))
                deletes.Add(deferred);
        }

        var token = response.SyncToken ?? Model.SyncToken;
        Model.SyncToken = token;
        _store.SaveSync(upserts, deletes, token);

        // The token has moved past these tombstones, so the server will never resend them: they have
        // to survive a restart or the deleted resource would come back permanently.
        _store.SaveDeferredDeletes(_deferredDeletes);
    }

    private void CascadeCancel(OutboxCommand createCommand)
    {
        // Cancel the failed create, everything that referenced it, and — transitively — anything that
        // referenced those, so a whole offline-built subtree rolls back in one go.
        var doomedIds = new HashSet<string>();
        if (createCommand.TempId is { } rootTemp)
            doomedIds.Add(rootTemp);

        var doomed = new List<OutboxCommand> { createCommand };
        bool grew;
        do
        {
            grew = false;
            foreach (var c in _outbox)
            {
                if (doomed.Contains(c) || !ReferencesAny(c, doomedIds))
                    continue;
                doomed.Add(c);
                if (IsCreate(c) && c.TempId is { } temp)
                    doomedIds.Add(temp);
                grew = true;
            }
        }
        while (grew);

        foreach (var d in doomed)
        {
            // A cancelled reorder's other tasks keep a position the server will never be told about,
            // so put them back — except the ones being rolled back here anyway.
            if (d.Type == "item_reorder" && d.PriorJson is { } priors)
                RestorePositions(priors, doomedIds);

            if (IsCreate(d) && d.TempId is { } temp)
                RemoveObject(temp);
            _outbox.Remove(d);
            ForgetUndoable(d.Uuid);
        }
        _store.DeleteCommands(doomed.Select(c => c.Uuid).ToList());
    }

    private void RestorePositions(string priorJson, HashSet<string> skip)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(priorJson);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is not JsonArray priors)
            return;

        foreach (var entry in priors)
        {
            if (entry is not JsonObject obj || obj["id"] is not JsonValue idValue)
                continue;

            var id = idValue.ToString();
            if (skip.Contains(id) || Model.Get(ResourceType.Items, id) is null)
                continue;

            var copy = obj.DeepClone().AsObject();
            _store.PutResource(ResourceType.Items, id, copy.ToJsonString());
            Model.Upsert(ResourceType.Items, id, copy);
        }
    }

    /// <summary>
    /// Resources a queued command owns until it resolves. Keyed by type as well as id: Todoist ids
    /// are only unique within a resource type, so a pending label edit would otherwise shadow the
    /// server's change to a task of the same id — and that change is gone for good, because the
    /// sync token advances past it either way.
    /// </summary>
    private HashSet<ResourceKey> PendingResourceKeys()
    {
        var keys = new HashSet<ResourceKey>();
        foreach (var c in _outbox.Where(c => c.State == OutboxState.Pending))
        {
            if (c.TempId is not null)
            {
                keys.Add(new ResourceKey(ResourceTypeFor(c), c.TempId));
                continue;
            }

            // Only a command that actually mutated a local copy has something to protect. A command
            // aimed at a resource we never held must not shadow the server's version of it, which
            // would be dropped for good once the sync token advances past that change.
            if (c.PriorJson is not null && ParseArgs(c)["id"] is JsonValue v)
                keys.Add(new ResourceKey(ResourceTypeFor(c), v.ToString()));
        }
        return keys;
    }

    /// <summary>
    /// Tasks named by a queued reorder. Their position is ours until it lands, but nothing else
    /// about them is, so they are tracked apart from resources with a genuine pending edit.
    /// </summary>
    private HashSet<ResourceKey> PendingReorderKeys()
    {
        var keys = new HashSet<ResourceKey>();
        foreach (var c in _outbox.Where(c => c.State == OutboxState.Pending))
            foreach (var id in NestedIds(ParseArgs(c)))
                keys.Add(new ResourceKey(ResourceTypeFor(c), id));
        return keys;
    }

    /// <summary>Ids carried in a command's <c>items</c> array, as a reorder does.</summary>
    private static IEnumerable<string> NestedIds(JsonObject args)
    {
        if (args["items"] is not JsonArray items)
            yield break;

        foreach (var entry in items)
            if (entry is JsonObject o && o["id"] is JsonValue id)
                yield return id.ToString();
    }

    /// <summary>
    /// Forces one full sync when the cache predates a resource type the client now asks for.
    /// </summary>
    /// <remarks>
    /// An incremental sync returns only what has changed, and there is no per-resource watermark —
    /// so a type added in a later version never arrives for anyone who already synced, and the
    /// feature built on it stays silently dead. The singletons are the tell: every account has
    /// them, so one missing means the set has grown since this cache was written.
    /// </remarks>
    private void ResyncIfResourcesAreMissing()
    {
        if (Model.SyncToken == "*")
            return;

        var missing = Model.Get(ResourceType.User, ResourceType.User) is null
                      || Model.Get(ResourceType.UserPlanLimits, ResourceType.UserPlanLimits) is null;

        if (missing)
            Model.SyncToken = "*";
    }

    private void PurgeLocal()
    {
        _generation++;
        Model.Clear();
        Model.SyncToken = "*";
        _outbox.Clear();
        _deferredDeletes.Clear();
        _undoStack.Clear();
        _undoable.Clear();
        _completed.Clear();
        _store.Purge();
    }

    private void RemoveObject(string id)
    {
        if (Model.Find(id) is { } found)
        {
            _store.DeleteResource(found.Type, id);
            Model.Remove(found.Type, id);
        }
    }

    private void RemoveCommand(OutboxCommand cmd)
    {
        _outbox.Remove(cmd);
        _store.DeleteCommands([cmd.Uuid]);
    }

    /// <summary>
    /// Whether a command's prior state is the form a cascading delete records: one entry per
    /// resource, each naming the type it carries, since one delete can span projects, sections and
    /// tasks. A reorder stores an array too, but of bare tasks — reading that as a delete would put
    /// an undo barrier in front of a write that is perfectly reversible.
    /// </summary>
    private static bool IsCascadingDelete(string? priorJson)
        => priorJson is not null
           && TryParseNode(priorJson) is JsonArray entries
           && entries.Count > 0
           && entries.All(e => e is JsonObject entry && entry["resource"] is JsonObject);

    /// <summary>Remembers a destructive write so <see cref="Undo"/> can reverse it later.</summary>
    private void RecordUndoable(OutboxCommand cmd, string id, string? prior)
        => Remember(cmd.Uuid, new UndoableWrite(cmd.Type, id, prior));

    /// <summary>Marks a destructive write that <see cref="Undo"/> cannot reverse.</summary>
    private void RecordUndoBarrier(OutboxCommand cmd)
        => Remember(cmd.Uuid, new UndoableWrite(UndoBarrier, cmd.Uuid, null));

    /// <summary>
    /// Puts a write on the undo stack, dropping the oldest once it is deeper than anyone would
    /// reach. Barriers go on the same stack as everything else — closing recurring tasks makes one
    /// per keypress, so a stack that only ever grew would be a leak in a long session.
    /// </summary>
    private void Remember(string uuid, UndoableWrite write)
    {
        _undoStack.Add(uuid);
        _undoable[uuid] = write;

        while (_undoStack.Count > MaxUndoDepth)
        {
            _undoable.Remove(_undoStack[0]);
            _undoStack.RemoveAt(0);
        }
    }

    private void ForgetUndoable(string uuid)
    {
        _undoStack.Remove(uuid);
        _undoable.Remove(uuid);
    }

    /// <summary>
    /// Commits an optimistic write to the durable store and queues its command. Callers mutate the
    /// in-memory model only after this returns, so a store failure can't leave a change on screen
    /// that was never persisted or queued.
    /// </summary>
    private OutboxCommand Persist(string type, JsonObject args, string? tempId, string? prior, IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes)
    {
        var cmd = new OutboxCommand
        {
            Uuid = Guid.NewGuid().ToString(),
            Type = type,
            TempId = tempId,
            ArgsJson = args.ToJsonString(),
            PriorJson = prior,
        };
        _store.ApplyLocalWrite(cmd, upserts, deletes);
        _outbox.Add(cmd);
        return cmd;
    }

    private static bool ReferencesAny(OutboxCommand cmd, HashSet<string> ids)
    {
        var args = ParseArgs(cmd);
        foreach (var key in IdKeys)
            if (args.TryGetPropertyValue(key, out var n) && n is JsonValue v && ids.Contains(v.ToString()))
                return true;

        // Including a reorder's nested ids, so a doomed create takes its reorder with it.
        return NestedIds(args).Any(ids.Contains);
    }

    private static JsonObject ParseArgs(OutboxCommand cmd) => TryParse(cmd.ArgsJson) ?? new JsonObject();

    private static JsonObject? TryParse(string json) => TryParseNode(json) as JsonObject;

    private static JsonNode? TryParseNode(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsCreate(OutboxCommand cmd) => cmd.Type.EndsWith("_add", StringComparison.Ordinal);

    private static string ResourceTypeFor(OutboxCommand cmd) => cmd.Type.Split('_')[0] switch
    {
        "project" => ResourceType.Projects,
        "section" => ResourceType.Sections,
        "label" => ResourceType.Labels,
        "filter" => ResourceType.Filters,
        "reminder" => ResourceType.Reminders,
        _ => ResourceType.Items,
    };
}
