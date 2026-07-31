namespace Termyn.Core.Api;

/// <summary>Abstraction over the Todoist unified API v1 (sync endpoint).</summary>
public interface ITodoistApi
{
    /// <summary>
    /// Performs a combined read+write sync. Pass <c>"*"</c> as <paramref name="syncToken"/> for a
    /// full sync, or a previously returned token for an incremental sync. Pending
    /// <paramref name="commands"/> are sent in the same request.
    /// </summary>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    /// <exception cref="TodoistAuthException">The token was rejected (401/403).</exception>
    Task<SyncResponse> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct = default);

    /// <summary>
    /// Creates a task from raw quick-add text, letting the server do the natural-language parsing so
    /// it matches the web app exactly. Returns the created task. Online only.
    /// </summary>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    /// <exception cref="TodoistAuthException">The token was rejected (401/403).</exception>
    Task<ResourceChange> QuickAddAsync(string token, string text, CancellationToken ct = default);

    /// <summary>
    /// Fetches one page of tasks completed within a window. Completed tasks are not returned by
    /// incremental sync, so this is the only way to see them; it is called on demand, never on the
    /// sync loop.
    /// </summary>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    /// <exception cref="TodoistAuthException">The token was rejected (401/403).</exception>
    Task<CompletedPage> GetCompletedAsync(string token, CompletedQuery query, CancellationToken ct = default);

    /// <summary>Validates a token with a minimal probe. Returns <c>false</c> if rejected (401/403).</summary>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default);
}
