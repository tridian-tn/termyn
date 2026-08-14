using System.Text.Json.Nodes;

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

    /// <summary>
    /// Copies a comment's file into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// Written straight to the destination as it arrives rather than buffered: a file attachment
    /// runs to a hundred megabytes on a paid plan, and the whole point of fetching on request is
    /// that it never has to be held in memory.
    /// </remarks>
    /// <param name="progress">Told the running byte count, for a UI that has to show the wait</param>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    /// <exception cref="TodoistAuthException">The token was rejected (401/403).</exception>
    Task DownloadAsync(string token, string fileUrl, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Uploads a file and returns the <c>file_attachment</c> metadata to hand to a <c>note_add</c>.
    /// Online only, and by design: the upload has to finish before the command that references it
    /// can be queued.
    /// </summary>
    /// <param name="content">The file's bytes, read as they're sent</param>
    /// <param name="fileName">What to call it on the account</param>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    /// <exception cref="TodoistAuthException">The token was rejected (401/403).</exception>
    Task<JsonObject> UploadAsync(string token, Stream content, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Deletes an uploaded file by its url. Not reversible — Todoist has no undelete for uploads.
    /// </summary>
    /// <exception cref="TodoistNetworkException">Todoist was unreachable, timed out, or returned an error status.</exception>
    /// <exception cref="TodoistAuthException">The token was rejected (401/403).</exception>
    Task DeleteUploadAsync(string token, string fileUrl, CancellationToken ct = default);
}
