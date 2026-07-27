using Termyn.Core.Api;
using Termyn.Core.Platform;

namespace Termyn.Core.Tests;

internal sealed class FakeSecrets : ISecretStore
{
    public string? Stored = "tok";

    public string? GetToken() => Stored;
    public void SetToken(string token) => Stored = token;
    public void ClearToken() => Stored = null;
}

internal sealed class FakeApi : ITodoistApi
{
    /// <summary>Builds the response for each sync, given the commands that were flushed.</summary>
    public Func<IReadOnlyList<Command>, SyncResponse>? Next;

    public Exception? Throw;
    public bool AcceptToken = true;
    public int ValidateCalls;
    public IReadOnlyList<Command> LastCommands = [];

    public Task<SyncResponse> SyncAsync(string token, string syncToken, IReadOnlyList<string> resourceTypes, IReadOnlyList<Command> commands, CancellationToken ct = default)
    {
        LastCommands = commands;
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(Next is not null ? Next(commands) : new SyncResponse { SyncToken = syncToken });
    }

    public Task<bool> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        ValidateCalls++;
        return Throw is not null ? throw Throw : Task.FromResult(AcceptToken);
    }
}
