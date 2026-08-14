namespace Termyn.Core.Tests;

/// <summary>
/// Links out of a task's own description, which unlike Termyn's own may point anywhere on the web
/// — and so are held to the one thing that still matters when the host can't be.
/// </summary>
public class ExternalLinkTests
{
    [Theory]
    [InlineData("https://example.com/a/page")]
    [InlineData("http://example.com/a/page")]
    [InlineData("https://todoist.com")]
    [InlineData("http://192.168.0.1:8080/status")]
    public void A_web_address_is_opened(string url)
        => Assert.NotNull(Links.External(url));

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]   // opens a document off the disk
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ms-msdt:/id")]                            // a scheme with its own history
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_a_web_address_is_refused(string? url)
        => Assert.Null(Links.External(url));

    [Theory]
    [InlineData("https://app.todoist.com@evil.example/x")]
    [InlineData("http://www.microsoft.com@203.0.113.4/")]
    [InlineData("https://user:hunter2@example.com/x")]
    public void A_host_that_is_not_the_host_it_reads_as_is_refused(string url)
    {
        // Everything before the @ is discarded by the browser, so the address goes to what follows
        // it while reading as what precedes it — and the description this comes out of are written by
        // whoever shares the project. The credentials form is refused with it: those have no
        // business in a browser's history, and nothing legitimate in a description needs them.
        Assert.Null(Links.External(url));
    }

    [Fact]
    public void A_bare_network_path_is_refused()
    {
        // Uri reads this as a file on another machine, and opening it reaches across the network
        // to fetch whatever is there.
        Assert.Null(Links.External(@"\\somewhere\share\thing.lnk"));
        Assert.Null(Links.External("//somewhere/share/thing.lnk"));
    }

    [Fact]
    public void What_comes_back_is_the_parsed_form_rather_than_the_words_handed_in()
    {
        // The shell gets what was checked, not a string that happened to pass a check on the way
        // past — the two differ once encoding and case are settled.
        Assert.Equal("https://example.com/a%20path", Links.External("https://EXAMPLE.com/a path"));
    }

    [Fact]
    public void The_description_is_held_to_a_different_rule_from_Termyns_own_links()
    {
        // Openable answers "is this one of ours" against a list of three places, which a link
        // someone wrote in a description will never be. External asks only whether it is a page.
        const string url = "https://example.com/anything";

        Assert.Null(Links.Openable(url));
        Assert.NotNull(Links.External(url));
    }
}
