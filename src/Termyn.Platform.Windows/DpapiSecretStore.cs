using System.Security.Cryptography;
using System.Text;
using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>
/// Stores the API token encrypted with Windows DPAPI (current-user scope), so it is readable only
/// by the same Windows user on the same machine. The token is never written in plaintext.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Termyn.Token.v1");

    private readonly string _tokenPath;

    public DpapiSecretStore(IAppPaths paths)
        => _tokenPath = Path.Combine(paths.ConfigDirectory, "token.bin");

    public string? GetToken()
    {
        if (!File.Exists(_tokenPath))
            return null;

        try
        {
            var encrypted = File.ReadAllBytes(_tokenPath);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Undecryptable blob (e.g. copied from another user) or a transient file lock — treat as
            // no usable token so the app re-prompts instead of failing to start.
            return null;
        }
    }

    public void SetToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_tokenPath, encrypted);
    }

    public void ClearToken()
    {
        if (File.Exists(_tokenPath))
            File.Delete(_tokenPath);
    }
}
