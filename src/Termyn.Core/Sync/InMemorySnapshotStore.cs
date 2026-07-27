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

    public void SaveDeferredDeletes(IReadOnlyList<ResourceKey> keys) => _deferredDeletes = keys.ToList();

    public void SaveSync(IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes, string syncToken)
    {
        foreach (var u in upserts)
            _resources[(u.Type, u.Id)] = u.Json;
        foreach (var d in deletes)
            _resources.Remove((d.Type, d.Id));
        SyncToken = syncToken;
    }

    public long ApplyLocalWrite(OutboxCommand command, StoredResource? upsert, ResourceKey? delete)
    {
        if (upsert is { } u)
            _resources[(u.Type, u.Id)] = u.Json;
        if (delete is { } d)
            _resources.Remove((d.Type, d.Id));

        command.Seq = ++_seq;
        _outbox.Add(Clone(command));
        return command.Seq;
    }

    public void PutResource(string type, string id, string json) => _resources[(type, id)] = json;

    public void DeleteResource(string type, string id) => _resources.Remove((type, id));

    public void RenameResource(string type, string oldId, string newId)
    {
        if (_resources.Remove((type, oldId), out var json))
            _resources[(type, newId)] = json;
    }

    public void UpdateCommand(OutboxCommand command)
    {
        var i = _outbox.FindIndex(c => c.Seq == command.Seq);
        if (i >= 0)
            _outbox[i] = Clone(command);
    }

    public void DeleteCommands(IReadOnlyList<string> uuids)
        => _outbox.RemoveAll(c => uuids.Contains(c.Uuid));

    public void Purge()
    {
        _resources.Clear();
        _outbox.Clear();
        _deferredDeletes.Clear();
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
