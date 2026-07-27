namespace Termyn.Core.Api;

/// <summary>Abstraction over the Todoist unified API v1 (sync endpoint).</summary>
public interface ITodoistApi
{
    /// <summary>
    /// Performs a sync read. Pass <c>"*"</c> as <paramref name="syncToken"/> for a full sync, or a
    /// previously returned token for an incremental sync.
    /// </summary>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    /// <exception cref="TodoistAuthException">The token was rejected (401/403).</exception>
    Task<SyncResult> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, CancellationToken ct = default);

    /// <summary>Validates a token with a minimal probe. Returns <c>false</c> if rejected (401/403).</summary>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default);
}
