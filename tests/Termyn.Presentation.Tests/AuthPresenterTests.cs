using Termyn.Core.Api;
using Termyn.Core.Platform;
using Termyn.Presentation;

namespace Termyn.Presentation.Tests;

public class AuthPresenterTests
{
    [Fact]
    public async Task Valid_token_is_stored()
    {
        var secrets = new FakeSecrets();
        var presenter = new AuthPresenter(new FakeApi { Accept = true }, secrets);

        var result = await presenter.ValidateAndStoreAsync("tok");

        Assert.Equal(TokenValidationResult.Valid, result);
        Assert.Equal("tok", secrets.Stored);
    }

    [Fact]
    public async Task Rejected_token_is_not_stored()
    {
        var secrets = new FakeSecrets();
        var presenter = new AuthPresenter(new FakeApi { Accept = false }, secrets);

        var result = await presenter.ValidateAndStoreAsync("bad");

        Assert.Equal(TokenValidationResult.Rejected, result);
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public async Task Network_error_is_surfaced_and_not_stored()
    {
        var secrets = new FakeSecrets();
        var presenter = new AuthPresenter(new FakeApi { ThrowNetwork = true }, secrets);

        var result = await presenter.ValidateAndStoreAsync("tok");

        Assert.Equal(TokenValidationResult.NetworkError, result);
        Assert.Null(secrets.Stored);
    }

    [Fact]
    public async Task Blank_token_is_rejected_without_calling_the_api()
    {
        var secrets = new FakeSecrets();
        var api = new FakeApi { Accept = true };
        var presenter = new AuthPresenter(api, secrets);

        var result = await presenter.ValidateAndStoreAsync("   ");

        Assert.Equal(TokenValidationResult.Rejected, result);
        Assert.Null(secrets.Stored);
        Assert.Equal(0, api.ValidateCalls);
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public string? Stored;

        public string? GetToken() => Stored;
        public void SetToken(string token) => Stored = token;
        public void ClearToken() => Stored = null;
    }

    private sealed class FakeApi : ITodoistApi
    {
        public bool Accept = true;
        public bool ThrowNetwork;
        public int ValidateCalls;

        public Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
        {
            ValidateCalls++;
            return ThrowNetwork
                ? throw new TodoistNetworkException("offline", new InvalidOperationException())
                : Task.FromResult(Accept);
        }

        public Task<SyncResult> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, CancellationToken ct = default)
            => Task.FromResult(new SyncResult { SyncToken = "abc" });
    }
}
