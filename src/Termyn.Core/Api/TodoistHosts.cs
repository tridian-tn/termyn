namespace Termyn.Core.Api;

/// <summary>
/// Which addresses are Todoist's own, for deciding what may be sent the account's token.
/// </summary>
/// <remarks>
/// A comment's attachment url comes off the network, written by whoever made the comment, and on a
/// shared project that's anyone the project is shared with. Sending the token to wherever it points
/// would hand it to anybody who cared to point one at a server of their own. Here rather than in the
/// client so every front end makes the same check.
/// </remarks>
public static class TodoistHosts
{
    /// <summary>The domain Todoist serves the API and uploaded files from.</summary>
    private const string Domain = "todoist.com";

    /// <summary>
    /// Whether a host is Todoist's: <c>todoist.com</c> itself or any name under it.
    /// </summary>
    /// <remarks>
    /// Compared as a whole label, so <c>eviltodoist.com</c> isn't taken for it. A trailing dot, the
    /// fully qualified form, names the same host and is allowed for.
    /// </remarks>
    /// <param name="host">The host, as <see cref="Uri.IdnHost"/> gives it</param>
    public static bool IsTodoist(string host)
    {
        var name = host.TrimEnd('.');

        return name.Equals(Domain, StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a url may be sent the token: absolute, https, on the default port and on a Todoist host.
    /// </summary>
    /// <remarks>
    /// The scheme matters as much as the host, since a token sent over plain http is a token anyone
    /// on the way can read. Credentials in the url are irrelevant: they come before the <c>@</c>,
    /// and the host is what follows it.
    /// </remarks>
    /// <param name="url">Where a request is about to go</param>
    public static bool MayCarryToken(Uri url)
        => url.IsAbsoluteUri
           && url.Scheme == Uri.UriSchemeHttps
           && url.IsDefaultPort
           && IsTodoist(url.IdnHost);
}
