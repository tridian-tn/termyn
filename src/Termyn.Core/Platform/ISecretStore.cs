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

    /// <summary>Persists the token securely, replacing any existing value.</summary>
    void SetToken(string token);

    /// <summary>Removes any stored token.</summary>
    void ClearToken();
}
