using Termyn.Core.Api;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

public class AuthPresenterTests
{
    [Fact]
    public async Task Valid_token_is_stored()
    {
        var secrets = new FakeSecrets { Stored = null };
        var presenter = new AuthPresenter(new FakeApi { AcceptToken = true }, secrets);

        var result = await presenter.ValidateAndStoreAsync("tok");

        Assert.Equal(TokenValidationResult.Valid, result);
        Assert.Equal("tok", secrets.Stored);
    }

    [Fact]
    public async Task Rejected_token_is_not_stored()
    {
        var secrets = new FakeSecrets { Stored = null };
        var presenter = new AuthPresenter(new FakeApi { AcceptToken = false }, secrets);

        var result = await presenter.ValidateAndStoreAsync("bad");

        Assert.Equal(TokenValidationResult.Rejected, result);
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public async Task Network_error_is_surfaced_and_not_stored()
    {
        var secrets = new FakeSecrets { Stored = null };
        var presenter = new AuthPresenter(new FakeApi { Throw = new TodoistNetworkException("offline") }, secrets);

        var result = await presenter.ValidateAndStoreAsync("tok");

        Assert.Equal(TokenValidationResult.NetworkError, result);
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public async Task Blank_token_is_rejected_without_calling_the_api()
    {
        var secrets = new FakeSecrets { Stored = null };
        var api = new FakeApi();
        var presenter = new AuthPresenter(api, secrets);

        var result = await presenter.ValidateAndStoreAsync("   ");

        Assert.Equal(TokenValidationResult.Rejected, result);
        Assert.Null(secrets.Stored);
        Assert.Equal(0, api.ValidateCalls);
    }
}
