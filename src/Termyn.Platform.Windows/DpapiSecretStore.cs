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

        // Written beside and moved into place, so a crash or a full disk part-way leaves the old
        // token or the new one, never a truncated blob that decrypts to nothing and signs the user
        // out. Beside rather than in %TEMP%, so the move is a rename on the same volume.
        var temporary = _tokenPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, _tokenPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Holds nothing the real file doesn't, and is written over by the next save.
        }
    }

    public void ClearToken()
    {
        if (File.Exists(_tokenPath))
            File.Delete(_tokenPath);
    }
}
