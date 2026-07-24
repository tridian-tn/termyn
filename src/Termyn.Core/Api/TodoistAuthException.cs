namespace Termyn.Core.Api;

/// <summary>Raised when Todoist rejects the supplied API token (HTTP 401/403).</summary>
public sealed class TodoistAuthException : Exception
{
    public TodoistAuthException(string message)
        : base(message)
    {
    }
}
