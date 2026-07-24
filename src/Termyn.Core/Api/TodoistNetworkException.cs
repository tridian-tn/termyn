namespace Termyn.Core.Api;

/// <summary>
/// Raised when a request could not be completed because Todoist was unreachable, timed out, or
/// answered with a non-success status (rate limit, 5xx) — as opposed to rejecting the token.
/// </summary>
public sealed class TodoistNetworkException : Exception
{
    public TodoistNetworkException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
