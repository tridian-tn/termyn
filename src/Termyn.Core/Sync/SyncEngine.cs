using System.Text.Json;
using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Model;
using Termyn.Core.Platform;

namespace Termyn.Core.Sync;

/// <summary>
/// Owns the offline-first sync loop: loads the snapshot, performs incremental sync, reconciles
/// server changes (upserts, tombstones, temp-id remapping) against optimistic local writes held in
/// a durable outbox, and flushes those writes as field-level commands.
/// </summary>
/// <remarks>
/// All model and store mutations are serialised on a single gate, so UI-thread writes and the
/// background sync worker cannot race. Only the network call happens outside the gate.
/// </remarks>
public sealed class SyncEngine
{
    private const int MaxCommandsPerSync = 100;

    /// <summary>Command argument fields that carry a resource id.</summary>
    private static readonly string[] IdKeys = ["id", "parent_id", "section_id", "project_id"];

    private readonly object _gate = new();
    private readonly ITodoistApi _api;
    private readonly ISnapshotStore _store;
    private readonly ISecretStore _secrets;
    private readonly int _attemptCeiling;
    private readonly List<OutboxCommand> _outbox = [];

    /// <summary>
    /// Deletions the server reported while the resource still had an un-acked local write. Tombstones
    /// arrive once, so they are held here and applied as soon as the blocking command clears.
    /// </summary>
    private readonly List<ResourceKey> _deferredDeletes = [];

    /// <summary>Uuids of destructive writes, most recent last, that <see cref="Undo"/> can reverse.</summary>
    private readonly List<string> _undoStack = [];

    /// <summary>What each undoable write did, so it can still be reversed after the server acks it.</summary>
    private readonly Dictionary<string, UndoableWrite> _undoable = [];

    private sealed record UndoableWrite(string Type, string Id, string? PriorJson);

    public SyncEngine(ITodoistApi api, ISnapshotStore store, ISecretStore secrets, int attemptCeiling = 5)
    {
        _api = api;
        _store = store;
        _secrets = secrets;
        _attemptCeiling = attemptCeiling;
    }

    public TodoistModel Model { get; } = new();

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

            foreach (var r in snapshot.Resources)
            {
                if (TryParse(r.Json) is { } o)
                    Model.Upsert(r.Type, r.Id, o);
            }

            foreach (var c in snapshot.Outbox)
            {
                if (TryParse(c.ArgsJson) is not null)
                    _outbox.Add(c);
            }
        }
    }

    /// <summary>Performs one incremental sync: flushes pending writes and reconciles the response.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        string token;
        string syncToken;
        List<OutboxCommand> pending;
        List<Command> commands;

        lock (_gate)
        {
            token = _secrets.GetToken()
                    ?? throw new InvalidOperationException("No Todoist token is stored.");
            syncToken = Model.SyncToken;
            pending = _outbox.Where(c => c.State == OutboxState.Pending).Take(MaxCommandsPerSync).ToList();
            commands = pending.Select(c => new Command(c.Type, c.Uuid, c.TempId, ParseArgs(c))).ToList();
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

        lock (_gate)
        {
            ApplyTempIds(response.TempIdMapping);
            ProcessCommandResults(response.SyncStatus, pending);
            ApplyServerChanges(response);
        }
    }

    // ---- Optimistic writes (field-level) ---------------------------------------------------------

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

            Persist("item_add", args, tempId, null, new StoredResource(ResourceType.Items, tempId, obj.ToJsonString()), null);
            Model.Upsert(ResourceType.Items, tempId, obj);
            return tempId;
        }
    }

    /// <summary>Applies changed fields to a task optimistically and queues an <c>item_update</c>.</summary>
    public void UpdateItem(string id, JsonObject changes)
    {
        lock (_gate)
        {
            var existing = Model.Get(ResourceType.Items, id);
            JsonObject? updated = null;
            string? prior = null;
            StoredResource? upsert = null;

            if (existing is not null)
            {
                prior = existing.ToJsonString();
                updated = existing.DeepClone().AsObject();
                foreach (var kv in changes)
                    updated[kv.Key] = kv.Value?.DeepClone();
                upsert = new StoredResource(ResourceType.Items, id, updated.ToJsonString());
            }

            var args = changes.DeepClone().AsObject();
            args["id"] = id;

            Persist("item_update", args, null, prior, upsert, null);
            if (updated is not null)
                Model.Upsert(ResourceType.Items, id, updated);
        }
    }

    /// <summary>Completes a task via <c>item_close</c>, which also advances a recurring task.</summary>
    /// <remarks>
    /// The task is marked complete locally straight away. A recurring task should instead show a
    /// transient "advancing" state until the server returns its next occurrence; that distinction
    /// arrives with the task UI.
    /// </remarks>
    public void CompleteItem(string id)
    {
        lock (_gate)
        {
            var existing = Model.Get(ResourceType.Items, id);
            JsonObject? completed = null;
            string? prior = null;
            StoredResource? upsert = null;

            if (existing is not null)
            {
                prior = existing.ToJsonString();
                completed = existing.DeepClone().AsObject();
                completed["checked"] = true;
                upsert = new StoredResource(ResourceType.Items, id, completed.ToJsonString());
            }

            var cmd = Persist("item_close", new JsonObject { ["id"] = id }, null, prior, upsert, null);
            if (completed is not null)
                Model.Upsert(ResourceType.Items, id, completed);
            RecordUndoable(cmd, id, prior);
        }
    }

    /// <summary>Reopens a completed task and queues an <c>item_uncomplete</c>.</summary>
    public void ReopenItem(string id)
    {
        lock (_gate)
        {
            var existing = Model.Get(ResourceType.Items, id);
            JsonObject? reopened = null;
            string? prior = null;
            StoredResource? upsert = null;

            if (existing is not null)
            {
                prior = existing.ToJsonString();
                reopened = existing.DeepClone().AsObject();
                reopened["checked"] = false;
                upsert = new StoredResource(ResourceType.Items, id, reopened.ToJsonString());
            }

            Persist("item_uncomplete", new JsonObject { ["id"] = id }, null, prior, upsert, null);
            if (reopened is not null)
                Model.Upsert(ResourceType.Items, id, reopened);
        }
    }

    /// <summary>Deletes a task optimistically and queues an <c>item_delete</c>.</summary>
    public void DeleteItem(string id)
    {
        lock (_gate)
        {
            var existing = Model.Get(ResourceType.Items, id);
            var prior = existing?.ToJsonString();
            ResourceKey? delete = existing is not null ? new ResourceKey(ResourceType.Items, id) : null;

            var cmd = Persist("item_delete", new JsonObject { ["id"] = id }, null, prior, null, delete);
            if (existing is not null)
                Model.Remove(ResourceType.Items, id);
            RecordUndoable(cmd, id, prior);
        }
    }

    /// <summary>
    /// Applies a new sibling order and queues an <c>item_reorder</c>. Ids are listed in their new
    /// order and given consecutive positions.
    /// </summary>
    public void ReorderItems(IReadOnlyList<string> orderedIds)
    {
        lock (_gate)
        {
            var entries = new JsonArray();
            var updated = new List<(string Id, JsonObject Json)>();

            for (var i = 0; i < orderedIds.Count; i++)
            {
                var id = orderedIds[i];
                entries.Add(new JsonObject { ["id"] = id, ["child_order"] = i + 1 });

                if (Model.Get(ResourceType.Items, id) is { } existing)
                {
                    var clone = existing.DeepClone().AsObject();
                    clone["child_order"] = i + 1;
                    updated.Add((id, clone));
                }
            }

            // Ordering spans several resources, so it is persisted as a plain batch of writes rather
            // than through the single-resource optimistic path.
            Persist("item_reorder", new JsonObject { ["items"] = entries }, null, null, null, null);
            foreach (var (id, json) in updated)
            {
                _store.PutResource(ResourceType.Items, id, json.ToJsonString());
                Model.Upsert(ResourceType.Items, id, json);
            }
        }
    }

    public bool CanUndo
    {
        get { lock (_gate) return _undoStack.Count > 0; }
    }

    /// <summary>
    /// Reverses the most recent completion or deletion. If its command hasn't reached the server it
    /// is simply dropped and the resource restored; if it has, the opposite command is issued.
    /// </summary>
    /// <returns><c>false</c> when there is nothing left to undo.</returns>
    public bool Undo()
    {
        lock (_gate)
        {
            if (_undoStack.Count == 0)
                return false;

            var uuid = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);

            if (_outbox.Any(c => c.Uuid == uuid))
            {
                Revert(uuid);
                return true;
            }

            // Already acknowledged by the server, so it has to be undone with its opposite.
            if (!_undoable.TryGetValue(uuid, out var record))
                return false;

            if (record.Type == "item_close")
            {
                ReopenItem(record.Id);
                return true;
            }

            // Todoist cannot undelete, so the task is recreated from the state we last held.
            if (record.PriorJson is { } json && TryParse(json) is { } fields)
            {
                fields.Remove("id");
                AddItem(fields);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Drops a queued command and restores the resource to the last state the server gave us, so
    /// the local copy returns to server truth rather than keeping a half-applied edit.
    /// </summary>
    public void Revert(string uuid)
    {
        lock (_gate)
        {
            var cmd = _outbox.FirstOrDefault(c => c.Uuid == uuid);
            if (cmd is null)
                return;

            if (IsCreate(cmd))
            {
                if (cmd.TempId is { } temp)
                    RemoveObject(temp);
            }
            else if (cmd.PriorJson is { } prior && TryParse(prior) is { } restored)
            {
                var type = ResourceTypeFor(cmd);
                if (restored["id"] is JsonValue idValue)
                {
                    var id = idValue.ToString();
                    _store.PutResource(type, id, prior);
                    Model.Upsert(type, id, restored);
                }
            }

            _outbox.Remove(cmd);
            _store.DeleteCommands([cmd.Uuid]);
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
                if (changed)
                {
                    cmd.ArgsJson = args.ToJsonString();
                    _store.UpdateCommand(cmd);
                }
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
                {
                    cmd.State = OutboxState.Failed;
                    cmd.LastError = "Todoist did not report a result for this change.";
                }
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
                cmd.State = OutboxState.Failed;
            _store.UpdateCommand(cmd);
        }
    }

    private void ApplyServerChanges(SyncResponse response)
    {
        var pendingIds = PendingResourceIds();
        var upserts = new List<StoredResource>();
        var deletes = new List<ResourceKey>();

        foreach (var change in response.Changes)
        {
            if (pendingIds.Contains(change.Id))
            {
                // Hold the deletion until the local write that protects this resource resolves.
                if (change.IsDeleted && !_deferredDeletes.Contains(new ResourceKey(change.ResourceType, change.Id)))
                    _deferredDeletes.Add(new ResourceKey(change.ResourceType, change.Id));
                continue;
            }

            if (change.IsDeleted)
            {
                if (Model.Remove(change.ResourceType, change.Id))
                    deletes.Add(new ResourceKey(change.ResourceType, change.Id));
            }
            else
            {
                var clone = change.Json.DeepClone().AsObject();
                Model.Upsert(change.ResourceType, change.Id, clone);
                upserts.Add(new StoredResource(change.ResourceType, change.Id, clone.ToJsonString()));
            }
        }

        for (var i = _deferredDeletes.Count - 1; i >= 0; i--)
        {
            var key = _deferredDeletes[i];
            if (pendingIds.Contains(key.Id))
                continue;
            _deferredDeletes.RemoveAt(i);
            if (Model.Remove(key.Type, key.Id))
                deletes.Add(key);
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
            if (IsCreate(d) && d.TempId is { } temp)
                RemoveObject(temp);
            _outbox.Remove(d);
        }
        _store.DeleteCommands(doomed.Select(c => c.Uuid).ToList());
    }

    private HashSet<string> PendingResourceIds()
    {
        var ids = new HashSet<string>();
        foreach (var c in _outbox.Where(c => c.State == OutboxState.Pending))
        {
            if (c.TempId is not null)
            {
                ids.Add(c.TempId);
                continue;
            }

            // Only a command that actually mutated a local copy has something to protect. A command
            // aimed at a resource we never held must not shadow the server's version of it, which
            // would be dropped for good once the sync token advances past that change.
            if (c.PriorJson is not null && ParseArgs(c)["id"] is JsonValue v)
                ids.Add(v.ToString());
        }
        return ids;
    }

    private void PurgeLocal()
    {
        Model.Clear();
        Model.SyncToken = "*";
        _outbox.Clear();
        _deferredDeletes.Clear();
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
    /// Commits an optimistic write to the durable store and queues its command. Callers mutate the
    /// in-memory model only after this returns, so a store failure can't leave a change on screen
    /// that was never persisted or queued.
    /// </summary>
    private void RecordUndoable(OutboxCommand cmd, string id, string? prior)
    {
        _undoStack.Add(cmd.Uuid);
        _undoable[cmd.Uuid] = new UndoableWrite(cmd.Type, id, prior);
    }

    private OutboxCommand Persist(string type, JsonObject args, string? tempId, string? prior, StoredResource? upsert, ResourceKey? delete)
    {
        var cmd = new OutboxCommand
        {
            Uuid = Guid.NewGuid().ToString(),
            Type = type,
            TempId = tempId,
            ArgsJson = args.ToJsonString(),
            PriorJson = prior,
        };
        _store.ApplyLocalWrite(cmd, upsert, delete);
        _outbox.Add(cmd);
        return cmd;
    }

    private static bool ReferencesAny(OutboxCommand cmd, HashSet<string> ids)
    {
        var args = ParseArgs(cmd);
        foreach (var key in IdKeys)
            if (args.TryGetPropertyValue(key, out var n) && n is JsonValue v && ids.Contains(v.ToString()))
                return true;
        return false;
    }

    private static JsonObject ParseArgs(OutboxCommand cmd) => TryParse(cmd.ArgsJson) ?? new JsonObject();

    private static JsonObject? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
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
