using System.ComponentModel;
using Termyn.Core;
using Termyn.Core.Platform;

namespace Termyn.App.Windows.Tests;

/// <summary>
/// The about box's page, built without being shown.
/// </summary>
/// <remarks>
/// Clicking a folder isn't exercised here, since that would open Explorer on whatever machine runs
/// the tests. What's checked is that each folder is offered as a link and that a link nobody wrote
/// opens nothing.
/// </remarks>
public class AboutBoxTests
{
    private static readonly IAppPaths Places = new Paths(@"C:\config", @"C:\cache", @"C:\logs");

    [WinFormsFact]
    public void The_version_is_the_heading()
    {
        using var page = AboutBox.Page(Places, 32);

        Assert.Equal($"Termyn {AppVersion.Tag}", page.Page.Heading);
    }

    [WinFormsFact]
    public void Its_words_come_from_the_catalogue()
    {
        using var page = AboutBox.Page(Places, 32);
        var diagnostics = page.Page.Expander!;

        Assert.Equal("About Termyn", Strings.AboutCaption);
        Assert.Equal(Strings.AboutCaption, page.Page.Caption);
        Assert.StartsWith(Strings.AboutTagline, page.Page.Text);
        Assert.Contains("Licences of included open-source components: <a href=\"notices\">", page.Page.Text);
        Assert.Equal(Strings.AboutShowDiagnostics, diagnostics.CollapsedButtonText);
        Assert.Equal(Strings.AboutHideDiagnostics, diagnostics.ExpandedButtonText);

        foreach (var label in new[] { Strings.AboutFolderInstalled, Strings.AboutFolderSettings, Strings.AboutFolderCache, Strings.AboutFolderLog })
            Assert.Contains($"{label}\n<a href=", diagnostics.Text);
    }

    [WinFormsFact]
    public void A_link_that_fails_is_reported_in_the_catalogue_s_words()
    {
        Assert.Equal(Strings.AboutHomepageFailed, AboutBox.Failure("home"));
        Assert.Equal(Strings.AboutNoticesMissing, AboutBox.Failure("notices"));
        Assert.Equal(Strings.AboutFolderMissing, AboutBox.Failure("log"));
    }

    [WinFormsFact]
    public void It_shows_the_app_icon_rather_than_a_stock_one()
    {
        using var page = AboutBox.Page(Places, 32);

        Assert.NotNull(page.Page.Icon);
        Assert.NotEqual(nint.Zero, page.Page.Icon.IconHandle);
    }

    [WinFormsFact]
    public void The_project_page_is_a_link_the_app_will_open()
    {
        // Links.Openable only takes paths under the repository's own folder, so the bare
        // repository address without its trailing slash would be refused when clicked.
        using var page = AboutBox.Page(Places, 32);

        Assert.True(page.Page.EnableLinks);
        Assert.Contains("<a href=\"home\">", page.Page.Text);
        Assert.NotNull(Links.Openable(AboutBox.Homepage));
    }

    [WinFormsFact]
    public void The_folders_are_links_tucked_away_under_diagnostics()
    {
        using var page = AboutBox.Page(Places, 32);
        var diagnostics = page.Page.Expander;

        Assert.NotNull(diagnostics);
        Assert.False(diagnostics.Expanded);

        foreach (var folder in new[] { AppVersion.Location, @"C:\config", @"C:\cache", @"C:\logs" })
        {
            Assert.Contains($">{folder}</a>", diagnostics.Text);
            Assert.DoesNotContain(folder, page.Page.Text);
        }
    }

    [WinFormsFact]
    public void A_link_the_shell_refuses_is_a_failure_rather_than_a_crash()
    {
        // What the shell throws when nothing's registered to open the thing. Escaping the dialog's
        // callback, it would reach WinForms' unhandled exception box.
        Assert.False(AboutBox.Opened(() => throw new Win32Exception(1155)));
        Assert.False(AboutBox.Opened(() => throw new InvalidOperationException()));
        Assert.True(AboutBox.Opened(() => true));
    }

    [WinFormsFact]
    public void Anything_else_thrown_is_left_to_surface()
    {
        // Only the shell's refusals are a link that couldn't be opened. A bug is still a bug.
        Assert.Throws<NullReferenceException>(() => AboutBox.Opened(() => throw new NullReferenceException()));
    }

    [WinFormsFact]
    public void A_link_it_did_not_write_opens_nothing()
    {
        Assert.False(AboutBox.Open("C:\\Windows\\System32\\calc.exe", Places));
        Assert.False(AboutBox.Open("https://example.com/", Places));
    }

    [WinFormsFact]
    public void A_folder_that_is_not_there_is_reported_rather_than_opened()
    {
        var missing = new Paths(@"C:\config", @"C:\cache", Path.Combine(Path.GetTempPath(), $"termyn-{Guid.NewGuid():N}"));

        Assert.False(AboutBox.Open("log", missing));
    }

    private sealed record Paths(string ConfigDirectory, string CacheDirectory, string LogDirectory) : IAppPaths
    {
        public string AttachmentDirectory => CacheDirectory;
        public string DownloadsDirectory => CacheDirectory;
    }
}
