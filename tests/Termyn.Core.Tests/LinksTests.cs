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
    [InlineData("https://github.com/someone-else/termyn/releases")]                  // not this project
    [InlineData("https://github.com/x/y/releases/download/v9/Termyn-setup.exe")]     // an arbitrary binary
    [InlineData("https://github.com/login/oauth/authorize?client_id=x&scope=repo")]  // an account prompt
    [InlineData("https://github.com/tridian-tn/termyn")]        // the repo itself, not a page under it
    [InlineData("https://app.todoist.com/oauth/authorize?client_id=x")]
    [InlineData("")]
    [InlineData(null)]
    public void A_link_that_is_not_ours_is_not_opened(string? url)
        => Assert.Null(Links.Openable(url));

    [Theory]
    [InlineData("https://anything@github.com/tridian-tn/termyn/releases/tag/v1.4.0")]
    [InlineData("https://user:secret@github.com/tridian-tn/termyn/releases")]
    [InlineData("https://user:secret@www.github.com/tridian-tn/termyn/releases")]
    [InlineData("https://someone@app.todoist.com/app/filters")]
    public void A_link_of_ours_carrying_credentials_is_not_opened(string url)
    {
        // The host in each of these is genuinely ours, so the host check has nothing to say about
        // them — and what is written before it rides along in the address handed to the shell,
        // into the browser's history and whatever its address bar shows. The other side of the
        // pair the host check already covers, where the real host is somebody else's.
        Assert.Null(Links.Openable(url));
    }

    [Theory]
    [InlineData("https://github.com/tridian-tn/termyn/releases")]
    [InlineData("https://www.github.com/tridian-tn/termyn/releases/tag/v1.4.0")]
    [InlineData("https://app.todoist.com/app/filters")]
    public void A_link_that_is_ours_is_opened(string url)
        => Assert.Equal(url, Links.Openable(url));

    [Fact]
    public void The_host_alone_is_not_enough_to_be_worth_opening()
    {
        // github.com isn't the project's own host — it's shared with every account on the site, and
        // it serves release assets, so a host check alone would still let a tampered response offer
        // an arbitrary unsigned download from a URL the dialog never shows.
        Assert.Null(Links.Openable("https://github.com/anyone/anything/releases/download/v1/setup.exe"));
        Assert.NotNull(Links.Openable("https://github.com/tridian-tn/termyn/releases/tag/v1.4.0"));
    }

    [Fact]
    public void Both_of_the_places_the_app_offers_to_open_are_openable()
    {
        // Built here and checked against the thing that guards them, so tightening one without the
        // other fails here rather than in the hands of whoever clicks the link.
        Assert.NotNull(Links.Openable(Links.TodoistFilter("276111043", "Assigned to me")));
        Assert.NotNull(Links.Openable(UpdateResult.ReleasesPage));
    }

    [Fact]
    public void A_filter_link_is_written_the_way_todoist_addresses_one()
    {
        // Name and id together, which is the form the app's own URLs take. Taken from a real one.
        Assert.Equal(
            "https://app.todoist.com/app/filter/assigned-to-me-276111043",
            Links.TodoistFilter("276111043", "Assigned to me"));
    }

    [Theory]
    [InlineData("Priority 1", "priority-1")]
    [InlineData("No due date", "no-due-date")]
    [InlineData("Tasks added today", "tasks-added-today")]
    [InlineData("  Leading and trailing  ", "leading-and-trailing")]
    [InlineData("Work / Home", "work-home")]
    [InlineData("Two--hyphens", "two-hyphens")]
    public void A_filters_name_is_slugified_into_the_link(string name, string slug)
        => Assert.Equal($"https://app.todoist.com/app/filter/{slug}-1", Links.TodoistFilter("1", name));

    [Theory]
    [InlineData("🔨")]
    [InlineData("")]
    [InlineData("---")]
    public void A_name_that_slugifies_to_nothing_leaves_the_id_to_identify_it(string name)
    {
        // Rather than a link beginning with a stray hyphen. Whatever the app makes of the slug, the
        // id is the part that says which filter this is.
        Assert.Equal("https://app.todoist.com/app/filter/1", Links.TodoistFilter("1", name));
    }

    [Fact]
    public void A_filter_link_survives_the_check_whatever_the_name_was()
    {
        // The name comes off the account and can hold anything at all, including the characters
        // that would change what a URL means. Nothing built here may come back refused.
        foreach (var name in new[] { "a/../../etc", "a?b=c", "a#b", "a b", "@ %", "..", "https://evil.example" })
            Assert.NotNull(Links.Openable(Links.TodoistFilter("1", name)));
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
