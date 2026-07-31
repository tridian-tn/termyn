namespace Termyn.Core;

/// <summary>
/// The links Termyn is willing to hand to the shell.
/// </summary>
/// <remarks>
/// Opening a link ends at ShellExecute, which runs a UNC path, a <c>file:</c> URL or a
/// <c>shell:</c> URL as readily as it opens a web page. Termyn only ever needs to open two places,
/// so rather than trusting each caller to have checked, everything goes through here and anything
/// else is refused. It lives in Core so the check has tests; the window's share is one call.
/// </remarks>
public static class Links
{
    /// <summary>The account's own filter page, which is where the saved filter lives.</summary>
    public const string TodoistFilters = "https://app.todoist.com/app/filters";

    private static readonly string[] Hosts = ["github.com", "www.github.com", "app.todoist.com"];

    /// <summary>
    /// A link worth opening, in the form to open it in.
    /// </summary>
    /// <param name="url">A candidate, which may have come off the network</param>
    /// <returns>The parsed and normalised URL, or null when it isn't one of ours</returns>
    public static string? Openable(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        // The normalised form rather than the string handed in: percent-encoding and case are
        // settled here, so what the shell receives is what was checked.
        return Array.Exists(Hosts, host => host == uri.Host) ? uri.AbsoluteUri : null;
    }
}
