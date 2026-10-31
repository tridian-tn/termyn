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

    /// <summary>
    /// Applies a batch of reconciled server changes, the new sync token and the deletions still
    /// waiting on a local write, atomically.
    /// </summary>
    /// <remarks>
    /// The waiting deletions go in the same transaction as the token because the token moving past
    /// a tombstone is what makes it unrepeatable: saved apart, a crash between the two lost the
    /// deletion for good and the resource came back.
    /// </remarks>
    /// <param name="upserts">Resources to write</param>
    /// <param name="deletes">Resources to remove</param>
    /// <param name="syncToken">Where the next sync starts from</param>
    /// <param name="deferredDeletes">
    /// Every deletion still waiting, replacing what was stored; null leaves the stored set alone
    /// </param>
    void SaveSync(IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes, string syncToken, IReadOnlyList<ResourceKey>? deferredDeletes = null);

    /// <summary>
    /// Commits an optimistic local mutation and its queued command together, so a crash can never
    /// leave a mutated resource without the command that would sync it. Returns the command's seq.
    /// </summary>
    long ApplyLocalWrite(OutboxCommand command, IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes);

    void PutResource(string type, string id, string json);
    void DeleteResource(string type, string id);
    void RenameResource(string type, string oldId, string newId);

    void UpdateCommand(OutboxCommand command);
    void DeleteCommands(IReadOnlyList<string> uuids);

    /// <summary>Erases all cached data: resources, outbox, withheld deletions and the sync token.</summary>
    void Purge();
}
