using System.Text;
using Termyn.Core.Update;

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
    /// <summary>
    /// The page for one saved filter in the Todoist app.
    /// </summary>
    /// <remarks>
    /// The filter itself, rather than the list of them. This is offered from a notice saying that
    /// this filter can't be read here, so landing on a page of all of them leaves the user to find
    /// it again — and the one they want is the one they just clicked.
    ///
    /// Todoist addresses a filter by its name and its id together, "assigned-to-me-276111043", so
    /// the name is slugified to match rather than the id being handed over on its own.
    /// </remarks>
    /// <param name="id">The filter's id</param>
    /// <param name="name">What the filter is called</param>
    /// <returns>The URL of that filter's page</returns>
    public static string TodoistFilter(string id, string name)
    {
        var slug = Slug(name);

        return slug.Length > 0
            ? $"https://app.todoist.com/app/filter/{slug}-{id}"
            : $"https://app.todoist.com/app/filter/{id}";
    }

    /// <summary>
    /// A filter's name in the form a URL carries it.
    /// </summary>
    /// <remarks>
    /// Runs of anything that isn't a letter or a digit collapse to one hyphen, and the ends are
    /// trimmed of them — so "Assigned to me" is "assigned-to-me". A name with nothing in it that
    /// survives, an emoji on its own say, leaves the id to identify the filter by itself.
    /// </remarks>
    private static string Slug(string name)
    {
        var slug = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
                slug.Append(char.ToLowerInvariant(c));
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        return slug.ToString().TrimEnd('-');
    }

    /// <summary>
    /// Where a link may point: a host, and the part of it we have any business opening.
    /// </summary>
    /// <remarks>
    /// The path matters as much as the host. github.com isn't the project's own host — it's shared
    /// with every account on the site, and it serves release assets and OAuth prompts, so a host
    /// check alone would still allow a link to an arbitrary unsigned download. The repository comes
    /// from the update check's own constant rather than being written out again here.
    /// </remarks>
    private static readonly (string Host, string Path)[] Allowed =
    [
        ("github.com", $"/{GitHubReleaseCheck.Repository}/"),
        ("www.github.com", $"/{GitHubReleaseCheck.Repository}/"),
        ("app.todoist.com", "/app/"),
    ];

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

        if (!Array.Exists(Allowed, place =>
                place.Host == uri.Host
                && uri.AbsolutePath.StartsWith(place.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // The normalised form rather than the string handed in: percent-encoding and case are
        // settled here, so what the shell receives is what was checked.
        return uri.AbsoluteUri;
    }

    /// <summary>
    /// A link out of the user's own description, in the form to open it in.
    /// </summary>
    /// <remarks>
    /// A different question from <see cref="Openable"/>, which asks whether a link is one of
    /// Termyn's own and answers against a list of three places. A description can point anywhere,
    /// so there is no host to check against — what there is instead is the scheme, and only the two
    /// that mean "a page". Everything else goes to the shell as a command: <c>file:</c> opens a
    /// document off the disk, and a bare UNC path reaches across the network to fetch one. A task
    /// description is text that arrives from an account, gets pasted into from anywhere, and syncs
    /// between machines; it does not get to say "run this".
    /// </remarks>
    /// <param name="url">A link written in a task's description</param>
    /// <returns>The parsed and normalised URL, or null when it isn't a web address</returns>
    public static string? External(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        // Both, unlike our own links: plenty of what people note down is still plain http, and
        // refusing to open it would only send them to copy it out by hand.
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;

        // A host that isn't the host it reads as. "https://app.todoist.com@evil.example/x" goes to
        // evil.example while reading as Todoist to anyone who doesn't parse URLs for a living, and
        // the description this comes from is written by whoever shares the project. Credentials get
        // refused with it, which is the other thing this component is used for and not something to
        // hand to a browser's address bar and history.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return null;

        return uri.AbsoluteUri;
    }
}
