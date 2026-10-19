using Termyn.Core.Api;
using Termyn.Core.Platform;

namespace Termyn.Presentation;

/// <summary>Outcome of a first-run token submission.</summary>
public enum TokenValidationResult
{
    Valid,
    Rejected,
    NetworkError,
}

/// <summary>What checking a pasted token found.</summary>
/// <param name="Result">Whether Todoist took it</param>
/// <param name="Token">The token as it'll be stored, trimmed</param>
/// <param name="UserId">The account it belongs to, when Todoist took it and said</param>
public sealed record TokenCheck(TokenValidationResult Result, string Token, string? UserId);

/// <summary>
/// Drives token entry: validate a pasted token, then persist it on success, or hand it back for a
/// reconnect to decide what to do with.
/// </summary>
public sealed class AuthPresenter
{
    private readonly ITodoistApi _api;
    private readonly ISecretStore _secrets;

    public AuthPresenter(ITodoistApi api, ISecretStore secrets)
    {
        _api = api;
        _secrets = secrets;
    }

    /// <summary>True when a token is already stored, so the token dialog can be skipped.</summary>
    public bool HasStoredToken => !string.IsNullOrEmpty(_secrets.GetToken());

    /// <summary>
    /// Validates the token with a probe and, if accepted, stores it securely. An unvalidated token
    /// is never persisted.
    /// </summary>
    public async Task<TokenValidationResult> ValidateAndStoreAsync(string token, CancellationToken ct = default)
    {
        var check = await ValidateAsync(token, ct);
        if (check.Result is TokenValidationResult.Valid)
            _secrets.SetToken(check.Token);
        return check.Result;
    }

    /// <summary>
    /// Validates the token with a probe and says whose it is, storing nothing. For a reconnect,
    /// where whether the token can be stored as it is depends on the account it turns out to be.
    /// </summary>
    public async Task<TokenCheck> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new TokenCheck(TokenValidationResult.Rejected, string.Empty, null);

        var trimmed = token.Trim();
        try
        {
            var probe = await _api.ValidateTokenAsync(trimmed, ct);
            return probe.Accepted
                ? new TokenCheck(TokenValidationResult.Valid, trimmed, probe.UserId)
                : new TokenCheck(TokenValidationResult.Rejected, trimmed, null);
        }
        catch (TodoistNetworkException)
        {
            return new TokenCheck(TokenValidationResult.NetworkError, trimmed, null);
        }
    }
}
