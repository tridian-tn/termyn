namespace Termyn.Core.Api;

/// <summary>
/// Raised when a request could not be completed because Todoist was unreachable, timed out, or
/// answered with a non-success status (rate limit, 5xx) — as opposed to rejecting the token.
/// </summary>
public class TodoistNetworkException : Exception
{
    public TodoistNetworkException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <param name="status">The status Todoist answered with, when it answered at all</param>
    public TodoistNetworkException(string message, int status, Exception? innerException = null)
        : base(message, innerException) => Status = status;

    /// <summary>
    /// The HTTP status Todoist answered with, or null when nothing answered.
    /// </summary>
    /// <remarks>
    /// The difference between "there is no connection" and "the server is having trouble", which
    /// look the same to a caller reading only the type. It matters to anything that has to tell the
    /// user what to do next: the first comes back on its own, the second may not.
    /// </remarks>
    public int? Status { get; }

    /// <summary>Whether nothing answered at all — no connection, or a request that timed out.</summary>
    public bool Unreachable => Status is null;
}

/// <summary>
/// Raised when Todoist answered <c>429</c>. Distinct from an ordinary failure because the fix is to
/// wait rather than to retry: the caller is being asked to stop for a while, not told something broke.
/// </summary>
public sealed class TodoistRateLimitException : TodoistNetworkException
{
    public TodoistRateLimitException(string message, TimeSpan? retryAfter)
        : base(message) => RetryAfter = retryAfter;

    /// <summary>How long the server asked us to wait, when it said. Null means it didn't.</summary>
    public TimeSpan? RetryAfter { get; }
}
