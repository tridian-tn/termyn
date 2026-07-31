using Termyn.Core;
using Termyn.Core.Update;

namespace Termyn.Core.Tests;

/// <summary>
/// The last check before a link reaches the shell.
/// </summary>
/// <remarks>
/// This lives in Core precisely so it can be tested: the window's own <c>OpenLink</c> is a call to
/// <see cref="Links.Openable"/> and a <c>Process.Start</c>, and the app project has no test project
/// of its own — so without this, the check standing between a tampered response and ShellExecute
/// would have no coverage at all.
/// </remarks>
public class LinksTests
{
    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\evil.example\share\Termyn-Update.exe")]
    [InlineData("shell:AppsFolder")]
    [InlineData("search-ms:query=passwords")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://github.com/tridian-tn/termyn/releases")]
    [InlineData("https://evil.example/termyn")]
    [InlineData("https://github.com.evil.example/termyn")]
    [InlineData("https://github.com@evil.example/termyn")]
    [InlineData("https://app.todoist.com.evil.example/app")]
    [InlineData("")]
    [InlineData(null)]
    public void A_link_that_is_not_ours_is_not_opened(string? url)
        => Assert.Null(Links.Openable(url));

    [Theory]
    [InlineData("https://github.com/tridian-tn/termyn/releases")]
    [InlineData("https://www.github.com/tridian-tn/termyn/releases/tag/v1.4.0")]
    [InlineData("https://app.todoist.com/app/filters")]
    public void A_link_that_is_ours_is_opened(string url)
        => Assert.Equal(url, Links.Openable(url));

    [Fact]
    public void Both_of_the_places_the_app_offers_to_open_are_openable()
    {
        // Written as constants and checked against the thing that guards them, so tightening one
        // without the other fails here rather than in the hands of whoever clicks the menu item.
        Assert.NotNull(Links.Openable(Links.TodoistFilters));
        Assert.NotNull(Links.Openable(UpdateResult.ReleasesPage));
    }

    [Fact]
    public void What_comes_back_is_the_parsed_form_rather_than_the_text_handed_in()
    {
        // The shell gets what was checked, not the string it was checked from — otherwise the two
        // could differ in exactly the ways that matter.
        Assert.Equal("https://github.com/tridian-tn/termyn/releases",
            Links.Openable("HTTPS://GitHub.com/tridian-tn/termyn/releases"));
    }
}
