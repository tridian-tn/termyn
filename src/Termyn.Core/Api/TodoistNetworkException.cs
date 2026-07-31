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
