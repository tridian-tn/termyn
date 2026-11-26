namespace Termyn.Core.Sync;

/// <summary>Non-durable <see cref="ISnapshotStore"/> for tests and ephemeral use.</summary>
public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly Dictionary<(string Type, string Id), string> _resources = new();
    private readonly List<OutboxCommand> _outbox = [];
    private List<ResourceKey> _deferredDeletes = [];
    private long _seq;

    public string SyncToken { get; private set; } = "*";

    public StoredSnapshot Load() => new()
    {
        SyncToken = SyncToken,
        Resources = _resources.Select(kv => new StoredResource(kv.Key.Type, kv.Key.Id, kv.Value)).ToList(),
        Outbox = _outbox.Select(Clone).ToList(),
        DeferredDeletes = _deferredDeletes.ToList(),
    };

    public void SaveSync(IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes, string syncToken, IReadOnlyList<ResourceKey>? deferredDeletes = null)
    {
        foreach (var u in upserts)
            Set((u.Type, u.Id), u.Json);
        foreach (var d in deletes)
            Unset((d.Type, d.Id));
        SyncToken = syncToken;

        if (deferredDeletes is not null)
            _deferredDeletes = deferredDeletes.ToList();
    }

    public long ApplyLocalWrite(OutboxCommand command, IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes)
    {
        foreach (var u in upserts)
            Set((u.Type, u.Id), u.Json);
        foreach (var d in deletes)
            Unset((d.Type, d.Id));

        command.Seq = ++_seq;
        NoteOutbox();
        _outbox.Add(Clone(command));
        return command.Seq;
    }

    public void PutResource(string type, string id, string json) => Set((type, id), json);

    public void DeleteResource(string type, string id) => Unset((type, id));

    public void RenameResource(string type, string oldId, string newId)
    {
        if (!_resources.TryGetValue((type, oldId), out var json)) return;

        Unset((type, oldId));
        Set((type, newId), json);
    }

    public void UpdateCommand(OutboxCommand command)
    {
        var i = _outbox.FindIndex(c => c.Seq == command.Seq);
        if (i < 0) return;

        NoteOutbox();
        _outbox[i] = Clone(command);
    }

    /// <remarks>
    /// Looked for from the newest end, where an undo takes them from, and no further than the last
    /// of them. Looked for through the whole queue, undoing a batch of thousands walked it once for
    /// each, and this store is what the tests time the engine with.
    /// </remarks>
    public void DeleteCommands(IReadOnlyList<string> uuids)
    {
        var left = uuids.ToHashSet(StringComparer.Ordinal);
        for (var i = _outbox.Count - 1; i >= 0 && left.Count > 0; i--)
        {
            if (!left.Remove(_outbox[i].Uuid)) continue;

            NoteOutbox();
            _outbox.RemoveAt(i);
        }
    }

    /// <remarks>
    /// Kept by noting what each thing was before the batch first changes it, to put back if it's
    /// never committed. Copied whole when it began, every batch paid for the size of the store
    /// rather than for what it wrote, and the tests time the engine through this.
    /// </remarks>
    public IStoreBatch BeginBatch()
    {
        if (_journal is not null) throw new InvalidOperationException("A batch is already open on this store.");

        _journal = new Journal(_deferredDeletes, _seq, SyncToken);
        return new Batch(this, _journal);
    }

    /// <summary>What the open batch has changed, or null outside one.</summary>
    private Journal? _journal;

    /// <summary>
    /// What a batch found before it changed it: each resource it touched, or null for one that
    /// wasn't there, and the queue once it first changes it.
    /// </summary>
    /// <remarks>
    /// The queue is kept as the list it was, not copies of what's in it, since the store replaces a
    /// command it holds rather than editing it. So are the withheld deletions, which a sync
    /// replaces whole.
    /// </remarks>
    private sealed class Journal(List<ResourceKey> deferredDeletes, long seq, string syncToken)
    {
        public Dictionary<(string Type, string Id), string?> Resources { get; } = new();

        public List<OutboxCommand>? Outbox { get; set; }

        public void PutBack(InMemorySnapshotStore store)
        {
            foreach (var (key, json) in Resources)
            {
                if (json is null) store._resources.Remove(key);
                else store._resources[key] = json;
            }

            if (Outbox is not null)
            {
                store._outbox.Clear();
                store._outbox.AddRange(Outbox);
            }

            store._deferredDeletes = deferredDeletes;
            store._seq = seq;
            store.SyncToken = syncToken;
        }
    }

    /// <summary>The batch <see cref="BeginBatch"/> hands out.</summary>
    /// <remarks>
    /// It only ever acts on its own journal, so one committed late or disposed twice can't put back
    /// or keep a batch opened after it. Committed once it's over, it throws, as a transaction does.
    /// </remarks>
    private sealed class Batch(InMemorySnapshotStore store, Journal journal) : IStoreBatch
    {
        private bool _over;

        public void Commit()
        {
            if (_over) throw new InvalidOperationException("This batch has already been committed or thrown away.");

            _over = true;
            if (store._journal == journal) store._journal = null;
        }

        public void Dispose()
        {
            if (_over) return;

            _over = true;
            if (store._journal != journal) return;

            store._journal = null;
            journal.PutBack(store);
        }
    }

    /// <summary>Stores a resource, noting what was there first when a batch is open.</summary>
    private void Set((string Type, string Id) key, string json)
    {
        Note(key);
        _resources[key] = json;
    }

    /// <summary>Takes a resource away, noting what was there first when a batch is open.</summary>
    private void Unset((string Type, string Id) key)
    {
        Note(key);
        _resources.Remove(key);
    }

    /// <summary>Notes what a resource is before a batch first changes it.</summary>
    private void Note((string Type, string Id) key)
    {
        if (_journal is null || _journal.Resources.ContainsKey(key)) return;

        _journal.Resources[key] = _resources.GetValueOrDefault(key);
    }

    /// <summary>Notes what the queue is before a batch first changes it.</summary>
    private void NoteOutbox()
    {
        if (_journal is not null) _journal.Outbox ??= _outbox.ToList();
    }

    /// <remarks>Refused with a batch open, which couldn't put back everything it took.</remarks>
    public void Purge()
    {
        if (_journal is not null) throw new InvalidOperationException("The store can't be emptied with a batch open.");

        _resources.Clear();
        _outbox.Clear();
        _deferredDeletes = [];
        SyncToken = "*";
    }

    public void Dispose()
    {
    }

    private static OutboxCommand Clone(OutboxCommand c) => new()
    {
        Seq = c.Seq,
        Uuid = c.Uuid,
        Type = c.Type,
        TempId = c.TempId,
        ArgsJson = c.ArgsJson,
        PriorJson = c.PriorJson,
        Attempts = c.Attempts,
        NoVerdictRounds = c.NoVerdictRounds,
        State = c.State,
        LastError = c.LastError,
    };
}
