using Termyn.Core.Platform;
using Termyn.Core.Update;

namespace Termyn.App.Windows;

/// <summary>
/// The about box: what this is, where it's from, and where it keeps its files.
/// </summary>
/// <remarks>
/// A task dialog rather than a message box, for four things a message box can't do: a heading in
/// the larger type, the app's own icon, links, and a section that starts collapsed. The folders go
/// in that section — they're what someone reporting a problem gets asked for, and nobody else
/// needs them in the way.
///
/// Each link carries a key rather than the path or address it opens, so what a click can reach is
/// the short list in <see cref="Open"/> and not whatever the text happens to say.
/// </remarks>
internal static class AboutBox
{
    /// <summary>
    /// The project's page. The trailing slash isn't decoration: <c>Links.Openable</c> only takes
    /// addresses under the repository's own folder, and the bare one isn't.
    /// </summary>
    internal const string Homepage = $"https://github.com/{GitHubReleaseCheck.Repository}/";

    /// <summary>The licences file, which sits beside the executable.</summary>
    private const string NoticesFile = "THIRD-PARTY-NOTICES.txt";

    /// <summary>The main icon's edge at 100% scale, which is what a task dialog draws it at.</summary>
    private const int IconSize = 32;

    /// <summary>
    /// Shows the box over the window, opening whatever's clicked in it.
    /// </summary>
    /// <param name="owner">The window it belongs to</param>
    /// <param name="paths">Where the app keeps its files</param>
    /// <param name="failed">Told what couldn't be opened, so the window can say so</param>
    internal static void Show(Control owner, IAppPaths paths, Action<string> failed)
    {
        using var about = Page(paths, owner.LogicalToDeviceUnits(IconSize));

        about.Page.LinkClicked += (_, e) =>
        {
            if (!Open(e.LinkHref, paths))
                failed(e.LinkHref);
        };

        TaskDialog.ShowDialog(owner, about.Page, TaskDialogStartupLocation.CenterOwner);
    }

    /// <summary>
    /// Builds the box's page without showing it.
    /// </summary>
    /// <param name="paths">Where the app keeps its files</param>
    /// <param name="iconSize">The icon's edge in pixels, for the screen it'll be shown on</param>
    /// <returns>The page, and the icon it borrows, to be disposed together once it's closed</returns>
    internal static AboutPage Page(IAppPaths paths, int iconSize)
    {
        // At the size it'll be drawn rather than the icon's default, which a task dialog would
        // stretch on a scaled screen.
        var icon = new Icon(AppIcon.Window, iconSize, iconSize);

        // One line between folders rather than a blank one, so opening the section doesn't stretch
        // the dialog more than it has to.
        var diagnostics = string.Join(
            "\n",
            Folders(paths).Select(folder => $"{folder.Label}:\n<a href=\"{folder.Key}\">{folder.Path}</a>"));

        var page = new TaskDialogPage
        {
            Caption = "About Termyn",
            Heading = $"Termyn {AppVersion.Tag}",
            Icon = new TaskDialogIcon(icon),
            Text = "A keyboard-driven Todoist client for Windows.\n\n"
                + $"<a href=\"home\">{Homepage.TrimEnd('/')}</a>\n\n"

                // Linked rather than shown: the notices run to three hundred lines, most of them
                // the Apache licence, which is no size for a dialog.
                + $"Licences of included open-source components: <a href=\"notices\">{NoticesFile}</a>",
            EnableLinks = true,
            AllowCancel = true,
            Buttons = { TaskDialogButton.OK },
            Expander = new TaskDialogExpander
            {
                Text = diagnostics,
                CollapsedButtonText = "Show diagnostics",
                ExpandedButtonText = "Hide diagnostics",
                Expanded = false,
            },
        };

        return new AboutPage(page, icon);
    }

    /// <summary>
    /// Opens what a link in the box points at.
    /// </summary>
    /// <param name="key">The link's key, as the box wrote it</param>
    /// <param name="paths">Where the app keeps its files</param>
    /// <returns>False when the key isn't one of the box's own, or what it names couldn't be opened</returns>
    internal static bool Open(string key, IAppPaths paths) => key switch
    {
        "home" => AppVersion.OpenLink(Homepage),
        "notices" => AppVersion.OpenFile(Path.Combine(AppVersion.Location, NoticesFile)),
        _ => Folders(paths).FirstOrDefault(folder => folder.Key == key) is { Path: { } path }
            && AppVersion.OpenFolder(path),
    };

    /// <summary>The folders the diagnostics section offers, in the order it lists them.</summary>
    private static IEnumerable<(string Key, string Label, string Path)> Folders(IAppPaths paths) =>
    [
        ("installed", "Installed in", AppVersion.Location),
        ("settings", "Settings and token", paths.ConfigDirectory),
        ("cache", "Cache", paths.CacheDirectory),

        // The first thing to ask for when somebody reports a problem.
        ("log", "Log", paths.LogDirectory),
    ];
}

/// <summary>The about box's page and the icon it shows, which outlives the page's own use of it.</summary>
/// <param name="Page">The page to show</param>
/// <param name="Icon">The icon the page borrows a handle from</param>
internal sealed record AboutPage(TaskDialogPage Page, Icon Icon) : IDisposable
{
    public void Dispose()
    {
        Page.Icon?.Dispose();
        Icon.Dispose();
    }
}
