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

    /// <summary>What the server returns for a quick add; unset means it is unreachable.</summary>
    public Func<string, ResourceChange>? QuickAdd;

    public int QuickAddCalls;

    public Task<ResourceChange> QuickAddAsync(string token, string text, CancellationToken ct = default)
    {
        QuickAddCalls++;
        if (Throw is not null)
            throw Throw;
        return QuickAdd is not null
            ? Task.FromResult(QuickAdd(text))
            : throw new TodoistNetworkException("offline");
    }
}
