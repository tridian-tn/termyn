using System.Security.Cryptography;
using System.Text;
using Termyn.Core.Logging;
using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>
/// Stores the API token encrypted with Windows DPAPI (current-user scope), so it is readable only
/// by the same Windows user on the same machine. The token is never written in plaintext.
/// </summary>
/// <remarks>
/// The file's read until the token's had, which is then kept for the session, as the Keychain store
/// keeps its. Read on every sync, a virus scanner or a backup opening it at the wrong moment held
/// syncing up mid-session; now only reading it at the start can meet one.
/// </remarks>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Termyn.Token.v1");

    /// <summary>ERROR_SHARING_VIOLATION as an HRESULT: another program has the file open.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>ERROR_LOCK_VIOLATION as an HRESULT: another program has locked part of the file.</summary>
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>How many times a save or a delete is tried while another program has the file.</summary>
    private const int Attempts = 10;

    /// <summary>The wait between those tries, which brings them to about half a second.</summary>
    private static readonly TimeSpan BetweenAttempts = TimeSpan.FromMilliseconds(50);

    private readonly string _tokenPath;
    private readonly ILog? _log;
    private readonly Lock _gate = new();

    /// <summary>Whether <see cref="_held"/> is the answer, so the file needn't be read again.</summary>
    private bool _known;

    /// <summary>The token, once it's been read or saved; null once it's been cleared.</summary>
    private string? _held;

    /// <summary>The last problem written down, so a file held for a minute is one line in the log.</summary>
    private string? _lastProblem;

    /// <summary>Keeps the token in a file in the config folder.</summary>
    /// <param name="paths">Where the config folder is</param>
    /// <param name="log">Where a token file that can't be read is written down, or null for nowhere</param>
    public DpapiSecretStore(IAppPaths paths, ILog? log = null)
    {
        _tokenPath = Path.Combine(paths.ConfigDirectory, "token.bin");
        _log = log;
    }

    public string? GetToken() => ReadToken().Token;

    public TokenRead ReadToken()
    {
        lock (_gate)
        {
            if (_known) return new(_held);

            var read = ReadFile();
            if (read.Token is not null)
            {
                _held = read.Token;
                _known = true;
            }

            return read;
        }
    }

    /// <summary>Reads the token off the disk. Called with the lock held.</summary>
    private TokenRead ReadFile()
    {
        byte[] encrypted;
        try
        {
            encrypted = File.ReadAllBytes(_tokenPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _lastProblem = null;
            return new(null);
        }
        catch (IOException ex) when (IsHeldOpen(ex))
        {
            // Another program has the file open just now, a virus scanner or a backup say. It's
            // still the account's token, and it'll be readable once they let go.
            Problem("The saved token is open in another program, so it can't be read yet.", ex);
            return new(null, Unreachable: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Access refused, a folder where the file should be, a disk error: nothing waiting puts
            // right. So it's as good as no token, and asking for another at least says something,
            // where waiting would wait for ever.
            Problem("The saved token can't be read, so a new one will be asked for.", ex);
            return new(null);
        }

        try
        {
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            _lastProblem = null;
            return new(Encoding.UTF8.GetString(bytes));
        }
        catch (CryptographicException ex)
        {
            // Copied from another account, or left by a reset profile: this user can never decrypt
            // it, so it's as good as no token too.
            Problem("The saved token can't be decrypted by this Windows user, so a new one will be asked for.", ex);
            return new(null);
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
        lock (_gate)
        {
            try
            {
                File.WriteAllBytes(temporary, encrypted);

                // Moving over a file another program has open is refused as access denied rather
                // than as a sharing violation, so that's tried again too.
                WhileHeldOpen(() => File.Move(temporary, _tokenPath, overwrite: true), deniedToo: true);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            _held = token;
            _known = true;
            _lastProblem = null;
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
        lock (_gate)
        {
            // Forgotten first, so the session goes on without it even if the file can't be deleted
            // yet: a token Todoist has turned away isn't sent again while it waits to go.
            _held = null;
            _known = true;

            WhileHeldOpen(() => { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); }, deniedToo: false);
        }
    }

    /// <summary>
    /// Does something to the file, trying again for a moment while another program has it open.
    /// </summary>
    /// <remarks>
    /// A scanner or a backup usually lets go within milliseconds. Giving up at the first refusal
    /// failed a sign-out that had already emptied the cache, which left the account signed in.
    /// </remarks>
    /// <param name="io">What to do to the file</param>
    /// <param name="deniedToo">Whether access denied is tried again as well, as it is for a move</param>
    private static void WhileHeldOpen(Action io, bool deniedToo)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                io();
                return;
            }
            catch (Exception ex) when (attempt < Attempts && (ex is IOException held && IsHeldOpen(held) || deniedToo && ex is UnauthorizedAccessException))
            {
                Thread.Sleep(BetweenAttempts);
            }
        }
    }

    private static bool IsHeldOpen(IOException ex) => ex.HResult is SharingViolation or LockViolation;

    /// <summary>Writes down why the token can't be had, once for as long as the reason stays the same.</summary>
    /// <param name="message">What it means for the account</param>
    /// <param name="error">What reading the file threw</param>
    private void Problem(string message, Exception error)
    {
        if (message == _lastProblem) return;

        _lastProblem = message;
        _log?.Warn(message, error);
    }
}
