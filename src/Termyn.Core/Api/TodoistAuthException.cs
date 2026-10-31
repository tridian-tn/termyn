namespace Termyn.Core.Api;

/// <summary>
/// Raised when Todoist rejects the supplied API token: a 401, or a 403 from the account-wide sync and
/// completed-tasks endpoints. A 403 on a file or upload is about the file, and isn't this.
/// </summary>
public sealed class TodoistAuthException : Exception
{
    public TodoistAuthException(string message)
        : base(message)
    {
    }
}
