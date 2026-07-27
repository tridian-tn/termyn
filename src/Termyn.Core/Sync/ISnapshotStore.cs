namespace Termyn.Core.Sync;

/// <summary>A persisted resource: its raw JSON keyed by type + id.</summary>
public sealed record StoredResource(string Type, string Id, string Json);

/// <summary>Identifies a resource for deletion.</summary>
public readonly record struct ResourceKey(string Type, string Id);

/// <summary>Everything needed to rebuild the in-memory model at startup.</summary>
public sealed class StoredSnapshot
{
    public string SyncToken { get; init; } = "*";
    public IReadOnlyList<StoredResource> Resources { get; init; } = [];
    public IReadOnlyList<OutboxCommand> Outbox { get; init; } = [];

    /// <summary>Server deletions withheld because the resource still had an un-acked local write.</summary>
    public IReadOnlyList<ResourceKey> DeferredDeletes { get; init; } = [];
}

/// <summary>
/// Durable persistence for the raw-resource snapshot, the sync token, and the command outbox.
/// The engine keeps the authoritative model in memory and writes through to this store.
/// </summary>
public interface ISnapshotStore : IDisposable
{
    StoredSnapshot Load();

    /// <summary>Applies a batch of reconciled server changes and the new sync token atomically.</summary>
    void SaveSync(IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes, string syncToken);

    /// <summary>
    /// Commits an optimistic local mutation and its queued command together, so a crash can never
    /// leave a mutated resource without the command that would sync it. Returns the command's seq.
    /// </summary>
    long ApplyLocalWrite(OutboxCommand command, StoredResource? upsert, ResourceKey? delete);

    void PutResource(string type, string id, string json);
    void DeleteResource(string type, string id);
    void RenameResource(string type, string oldId, string newId);

    void UpdateCommand(OutboxCommand command);
    void DeleteCommands(IReadOnlyList<string> uuids);

    /// <summary>
    /// Records the deletions still waiting on a local write. They must outlive a restart: the sync
    /// token has already advanced past the tombstone, so the server will never resend it.
    /// </summary>
    void SaveDeferredDeletes(IReadOnlyList<ResourceKey> keys);

    /// <summary>Erases all cached data: resources, outbox, withheld deletions and the sync token.</summary>
    void Purge();
}
