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

/// <summary>Drives first-run authentication: validate a pasted token, then persist it on success.</summary>
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
        if (string.IsNullOrWhiteSpace(token))
            return TokenValidationResult.Rejected;

        var trimmed = token.Trim();
        try
        {
            if (!await _api.ValidateTokenAsync(trimmed, ct))
                return TokenValidationResult.Rejected;
        }
        catch (TodoistNetworkException)
        {
            return TokenValidationResult.NetworkError;
        }

        _secrets.SetToken(trimmed);
        return TokenValidationResult.Valid;
    }
}
