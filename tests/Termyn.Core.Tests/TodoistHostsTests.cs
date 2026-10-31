using Termyn.Core.Api;

namespace Termyn.Core.Tests;

public class TodoistHostsTests
{
    [Theory]
    [InlineData("todoist.com")]
    [InlineData("files.todoist.com")]
    [InlineData("FILES.Todoist.COM")]
    [InlineData("files.todoist.com.")]
    public void Todoist_and_the_names_under_it_are_todoist(string host)
        => Assert.True(TodoistHosts.IsTodoist(host));

    [Theory]
    [InlineData("eviltodoist.com")]
    [InlineData("todoist.com.evil.example")]
    [InlineData("todoist.co")]
    [InlineData("files.todoist.test")]
    [InlineData("")]
    public void A_name_that_only_looks_like_it_is_not(string host)
        => Assert.False(TodoistHosts.IsTodoist(host));

    [Theory]
    [InlineData("https://files.todoist.com/a.pdf", true)]
    [InlineData("https://api.todoist.com/api/v1/uploads", true)]
    [InlineData("http://files.todoist.com/a.pdf", false)]
    [InlineData("https://files.todoist.com:8443/a.pdf", false)]
    [InlineData("https://files.todoist.com@evil.example/a.pdf", false)]
    [InlineData("https://evil.example/files.todoist.com", false)]
    public void Only_https_to_a_todoist_host_may_carry_the_token(string url, bool may)
        => Assert.Equal(may, TodoistHosts.MayCarryToken(new Uri(url)));

    [Fact]
    public void A_relative_url_may_not_carry_it()
        => Assert.False(TodoistHosts.MayCarryToken(new Uri("/a.pdf", UriKind.Relative)));
}
