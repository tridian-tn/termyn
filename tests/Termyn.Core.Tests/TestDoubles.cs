using Termyn.Core.Api;
using Termyn.Core.Platform;
using Termyn.Core.Sync;

namespace Termyn.Core.Tests;

internal sealed class FakeSecrets : ISecretStore
{
    public string? Stored = "tok";

    public string? GetToken() => Stored;
    public void SetToken(string token) => Stored = token;
    public void ClearToken() => Stored = null;
}

/// <summary>An in-memory store whose durable write fails, standing in for a full or unwritable disk.</summary>
internal sealed class FailingWriteStore : ISnapshotStore
{
    private readonly InMemorySnapshotStore _inner = new();

    public StoredSnapshot Load() => _inner.Load();
    public void SaveSync(IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes, string syncToken) => _inner.SaveSync(upserts, deletes, syncToken);
    public void PutResource(string type, string id, string json) => _inner.PutResource(type, id, json);
    public void DeleteResource(string type, string id) => _inner.DeleteResource(type, id);
    public void RenameResource(string type, string oldId, string newId) => _inner.RenameResource(type, oldId, newId);
    public void UpdateCommand(OutboxCommand command) => _inner.UpdateCommand(command);
    public void DeleteCommands(IReadOnlyList<string> uuids) => _inner.DeleteCommands(uuids);
    public void SaveDeferredDeletes(IReadOnlyList<ResourceKey> keys) => _inner.SaveDeferredDeletes(keys);
    public void Purge() => _inner.Purge();
    public void Dispose() => _inner.Dispose();

    public long ApplyLocalWrite(OutboxCommand command, StoredResource? upsert, ResourceKey? delete)
        => throw new IOException("disk full");
}

internal sealed class FakeApi : ITodoistApi
{
    /// <summary>Builds the response for each sync, given the commands that were flushed.</summary>
    public Func<IReadOnlyList<Command>, SyncResponse>? Next;

    public Exception? Throw;
    public bool AcceptToken = true;
    public int ValidateCalls;
    public IReadOnlyList<Command> LastCommands = [];

    public Task<SyncResponse> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct = default)
    {
        LastCommands = commands;
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(Next is not null ? Next(commands) : new SyncResponse { SyncToken = syncToken });
    }

    public Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        ValidateCalls++;
        return Throw is not null ? throw Throw : Task.FromResult(AcceptToken);
    }
}
