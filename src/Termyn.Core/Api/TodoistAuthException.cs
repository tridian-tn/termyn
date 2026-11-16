namespace Termyn.Core.Api;

/// <summary>
/// Raised when there's no token to go on with: Todoist rejected it (a 401, or a 403 from the
/// account-wide sync and completed-tasks endpoints), none is stored, or the stored one can't be read
/// just now. A 403 on a file or upload is about the file, and isn't this.
/// </summary>
/// <remarks>
/// Only a rejection puts the account on hold, which the engine sees to. <see cref="Unreachable"/>
/// tells the last case from the other two, and isn't a reason to ask for another token.
/// </remarks>
public sealed class TodoistAuthException : Exception
{
    public TodoistAuthException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Whether nothing was sent because the stored token couldn't be read just now, rather than
    /// Todoist turning it away or there being none.
    /// </summary>
    /// <remarks>
    /// Nothing about the account has changed when this is set. It's still signed in, and the next
    /// try finds the token once whatever's holding it lets go.
    /// </remarks>
    public bool Unreachable { get; init; }
}
