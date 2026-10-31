namespace Termyn.Core.Platform;

/// <summary>Securely stores the Todoist API token for the current OS user.</summary>
/// <remarks>
/// Implemented per-platform (DPAPI on Windows; Keychain / Secret Service elsewhere). The portable
/// core depends only on this interface.
/// </remarks>
public interface ISecretStore
{
    /// <summary>Returns the stored token, or <c>null</c> if none is stored.</summary>
    string? GetToken();

    /// <summary>
    /// Returns the stored token, saying whether a token that can't be had is missing or only out of
    /// reach for now.
    /// </summary>
    /// <remarks>
    /// The two are opposite answers to whether the account is still signed in. A Keychain whose
    /// access prompt hasn't been answered, or a locked one, holds the token as surely as an open
    /// one does, and treating that as no token would put the account on hold over nothing. By
    /// default a store can't tell the two apart and reports every miss as missing, which is what
    /// <see cref="GetToken"/> has always meant.
    /// </remarks>
    TokenRead ReadToken() => new(GetToken());

    /// <summary>Persists the token securely, replacing any existing value.</summary>
    void SetToken(string token);

    /// <summary>Removes any stored token.</summary>
    void ClearToken();
}

/// <summary>What reading the stored token found.</summary>
/// <param name="Token">The token, or null when it couldn't be had</param>
/// <param name="Unreachable">
/// True when there is a token but it can't be read just now — a locked Keychain, or an access
/// prompt not yet answered — so it's worth asking again rather than asking the user for another
/// </param>
public readonly record struct TokenRead(string? Token, bool Unreachable = false)
{
    /// <summary>Whether a token is stored, readable now or not.</summary>
    public bool Stored => Token is not null || Unreachable;
}
