using Termyn.Core.Api;
using Termyn.Presentation;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

public class AuthPresenterTests
{
    [Fact]
    public void A_token_out_of_reach_is_still_a_token_so_it_isnt_asked_for_again()
    {
        // Asking would only store a second copy of what the Keychain already holds behind its prompt.
        Assert.True(new AuthPresenter(new FakeApi(), new FakeSecrets { Stored = "tok", Unreachable = true }).HasStoredToken);
        Assert.True(new AuthPresenter(new FakeApi(), new FakeSecrets { Stored = "tok" }).HasStoredToken);
        Assert.False(new AuthPresenter(new FakeApi(), new FakeSecrets { Stored = null }).HasStoredToken);
    }

    [Fact]
    public async Task Valid_token_is_stored()
    {
        var secrets = new FakeSecrets { Stored = null };
        var presenter = new AuthPresenter(new FakeApi { AcceptToken = true }, secrets);

        var result = await presenter.ValidateAndStoreAsync("tok", TestContext.Current.CancellationToken);

        Assert.Equal(TokenValidationResult.Valid, result);
        Assert.Equal("tok", secrets.Stored);
    }

    [Fact]
    public async Task Checking_a_token_says_whose_it_is_and_stores_nothing()
    {
        // A reconnect decides what to do with the token from whose it is, so it mustn't be stored
        // before then.
        var secrets = new FakeSecrets { Stored = null };
        var presenter = new AuthPresenter(new FakeApi { TokenUserId = "u7" }, secrets);

        var check = await presenter.ValidateAsync("  tok  ", TestContext.Current.CancellationToken);

        Assert.Equal(new TokenCheck(TokenValidationResult.Valid, "tok", "u7"), check);
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public async Task Rejected_token_is_not_stored()
    {
        var secrets = new FakeSecrets { Stored = null };
        var presenter = new AuthPresenter(new FakeApi { AcceptToken = false }, secrets);

        var result = await presenter.ValidateAndStoreAsync("bad", TestContext.Current.CancellationToken);

        Assert.Equal(TokenValidationResult.Rejected, result);
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public async Task Network_error_is_surfaced_and_not_stored()
    {
        var secrets = new FakeSecrets { Stored = null };
        var presenter = new AuthPresenter(new FakeApi { Throw = new TodoistNetworkException("offline") }, secrets);

        var result = await presenter.ValidateAndStoreAsync("tok", TestContext.Current.CancellationToken);

        Assert.Equal(TokenValidationResult.NetworkError, result);
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public async Task Blank_token_is_rejected_without_calling_the_api()
    {
        var secrets = new FakeSecrets { Stored = null };
        var api = new FakeApi();
        var presenter = new AuthPresenter(api, secrets);

        var result = await presenter.ValidateAndStoreAsync("   ", TestContext.Current.CancellationToken);

        Assert.Equal(TokenValidationResult.Rejected, result);
        Assert.Null(secrets.Stored);
        Assert.Equal(0, api.ValidateCalls);
    }
}
