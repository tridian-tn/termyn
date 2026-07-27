using System.Text.Json.Nodes;

namespace Termyn.Core.Api;

/// <summary>A write command sent to the Todoist sync endpoint. <paramref name="Args"/> carries only
/// the fields being changed, so unmodelled server fields are never overwritten.</summary>
public sealed record Command(string Type, string Uuid, string? TempId, JsonObject Args);

/// <summary>A single resource returned by a sync read — an upsert, or a tombstone when
/// <paramref name="IsDeleted"/> is true.</summary>
public sealed record ResourceChange(string ResourceType, string Id, bool IsDeleted, JsonObject Json);

/// <summary>Per-command outcome from a sync write.</summary>
public sealed record CommandResult(bool Ok, string? ErrorCode, string? Error);

/// <summary>The parsed result of a sync read+write round-trip.</summary>
public sealed class SyncResponse
{
    /// <summary>
    /// The token to use for the next incremental sync, or <c>null</c> when the response carried
    /// none. A missing token must not reset the caller's position, which would force a full sync.
    /// </summary>
    public string? SyncToken { get; init; }

    public bool FullSync { get; init; }
    public IReadOnlyList<ResourceChange> Changes { get; init; } = [];
    public IReadOnlyDictionary<string, CommandResult> SyncStatus { get; init; } = new Dictionary<string, CommandResult>();
    public IReadOnlyDictionary<string, string> TempIdMapping { get; init; } = new Dictionary<string, string>();
}
