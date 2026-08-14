using System.Text.Json.Nodes;
using Termyn.Core.Api;
using Termyn.Core.Platform;
using Termyn.Core.Sync;

namespace Termyn.TestSupport;

/// <summary>A clock stuck on one date, so date-sensitive tests are deterministic.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateOnly today) => Today = today;

    public DateOnly Today { get; }

    /// <summary>Midday, so converting into any timezone still lands on the same date.</summary>
    public DateTimeOffset UtcNow => new(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
}

public sealed class FakeSecrets : ISecretStore
{
    public string? Stored = "tok";

    public string? GetToken() => Stored;
    public void SetToken(string token) => Stored = token;
    public void ClearToken() => Stored = null;
}

public sealed class FakeApi : ITodoistApi
{
    /// <summary>Builds the response for each sync, given the commands that were flushed.</summary>
    public Func<IReadOnlyList<Command>, SyncResponse>? Next;

    /// <summary>Convenience for tests that always want the same response.</summary>
    public SyncResponse Response
    {
        set => Next = _ => value;
    }

    /// <summary>What the server returns for a quick add; unset means it is unreachable.</summary>
    public Func<string, ResourceChange>? QuickAdd;

    public Exception? Throw;
    public bool AcceptToken = true;
    public int ValidateCalls;
    public int QuickAddCalls;
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

    public Task<ResourceChange> QuickAddAsync(string token, string text, CancellationToken ct = default)
    {
        QuickAddCalls++;
        if (Throw is not null)
            throw Throw;
        return QuickAdd is not null
            ? Task.FromResult(QuickAdd(text))
            : throw new TodoistNetworkException("offline");
    }

    /// <summary>Builds the completed page for each request, so paging can be exercised.</summary>
    public Func<CompletedQuery, CompletedPage>? Completed;

    /// <summary>Every completed-items query made, in order, for asserting on scope and paging.</summary>
    public List<CompletedQuery> CompletedQueries = [];

    public Task<CompletedPage> GetCompletedAsync(string token, CompletedQuery query, CancellationToken ct = default)
    {
        CompletedQueries.Add(query);
        if (CompletedThrow is not null)
            throw CompletedThrow;
        return Task.FromResult(Completed is not null ? Completed(query) : new CompletedPage([], null));
    }

    /// <summary>Kept apart from <see cref="Throw"/> so a test can fail one call without failing sync.</summary>
    public Exception? CompletedThrow;

    // ---- Attachments ------------------------------------------------------------------------------

    /// <summary>What a download writes, or unset for a server that has no such file.</summary>
    public byte[]? FileBytes;

    /// <summary>Kept apart from <see cref="Throw"/> so a test can fail a transfer without failing sync.</summary>
    public Exception? TransferThrow;

    /// <summary>Every url downloaded, in order.</summary>
    public List<string> Downloaded = [];

    /// <summary>Every url whose upload was deleted, in order.</summary>
    public List<string> DeletedUploads = [];

    /// <summary>What each upload returns as its <c>file_attachment</c>, given the name it was sent.</summary>
    public Func<string, JsonObject>? Upload;

    /// <summary>The bytes each upload actually carried, so a test can check what was sent.</summary>
    public List<byte[]> Uploaded = [];

    public async Task DownloadAsync(string token, string fileUrl, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        Downloaded.Add(fileUrl);
        if (TransferThrow is not null)
            throw TransferThrow;

        var bytes = FileBytes ?? throw new TodoistNetworkException("no such file");

        // In two goes, so a test can see progress being reported rather than only its total.
        var half = bytes.Length / 2;
        await destination.WriteAsync(bytes.AsMemory(0, half), ct);
        progress?.Report(half);
        await destination.WriteAsync(bytes.AsMemory(half), ct);
        progress?.Report(bytes.Length);
    }

    public async Task<JsonObject> UploadAsync(string token, Stream content, string fileName, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        Uploaded.Add(buffer.ToArray());

        if (TransferThrow is not null)
            throw TransferThrow;

        return Upload is not null
            ? Upload(fileName)
            : new JsonObject
            {
                ["file_name"] = fileName,
                ["file_size"] = buffer.Length,
                ["file_type"] = "application/octet-stream",
                ["file_url"] = $"https://files.todoist.test/{fileName}",
                ["upload_state"] = "completed",
            };
    }

    public Task DeleteUploadAsync(string token, string fileUrl, CancellationToken ct = default)
    {
        DeletedUploads.Add(fileUrl);
        return TransferThrow is not null ? throw TransferThrow : Task.CompletedTask;
    }
}

/// <summary>An in-memory store whose durable write fails, standing in for a full or unwritable disk.</summary>
public sealed class FailingWriteStore : ISnapshotStore
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

    public long ApplyLocalWrite(OutboxCommand command, IReadOnlyList<StoredResource> upserts, IReadOnlyList<ResourceKey> deletes)
        => throw new IOException("disk full");
}

/// <summary>Shorthand for building the raw resource JSON tests feed into the model.</summary>
public static class Json
{
    public static JsonObject Object(string json) => (JsonObject)JsonNode.Parse(json)!;

    public static ResourceChange Change(string type, string id, string json)
        => new(type, id, false, Object(json));

    public static ResourceChange Deleted(string type, string id)
        => new(type, id, true, new JsonObject { ["id"] = id });
}
