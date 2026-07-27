using Termyn.Core.Api;
using Termyn.Core.Platform;

namespace Termyn.Presentation.Tests;

internal sealed class FakeSecrets : ISecretStore
{
    public string? Stored = "tok";

    public string? GetToken() => Stored;
    public void SetToken(string token) => Stored = token;
    public void ClearToken() => Stored = null;
}

internal sealed class FakeApi : ITodoistApi
{
    public SyncResponse Response = new() { SyncToken = "s" };
    public Exception? Throw;
    public bool AcceptToken = true;
    public int ValidateCalls;

    public Task<SyncResponse> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct = default)
        => Throw is not null ? throw Throw : Task.FromResult(Response);

    public Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        ValidateCalls++;
        return Throw is not null ? throw Throw : Task.FromResult(AcceptToken);
    }
}
