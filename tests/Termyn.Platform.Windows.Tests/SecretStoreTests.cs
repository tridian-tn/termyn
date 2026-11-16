using Termyn.Core.Platform;
using Termyn.Platform.Windows;
using Termyn.TestSupport;

namespace Termyn.Platform.Windows.Tests;

/// <summary>
/// The token's file: what a save that goes wrong part-way leaves in it, and what's read from it
/// while another program has it open.
/// </summary>
/// <remarks>
/// The store keeps the token once it's had it, so a fresh store stands in for the next launch
/// wherever what's on the disk is the question.
/// </remarks>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "termyn-secret-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLog _log = new();

    public SecretStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string TokenFile => Path.Combine(_directory, "token.bin");

    private DpapiSecretStore Store() => new(new Paths(_directory), _log);

    /// <summary>The file, held the way a virus scanner or a backup holds it.</summary>
    /// <param name="share">What the holder lets other programs do with it meanwhile</param>
    private FileStream Hold(FileShare share = FileShare.None) => new(TokenFile, FileMode.Open, FileAccess.Read, share);

    [Fact]
    public void A_token_saved_over_another_is_the_one_read_back()
    {
        var store = Store();
        store.SetToken("first");
        store.SetToken("second");

        Assert.Equal("second", Store().GetToken());
        Assert.Equal(["token.bin"], Directory.GetFileSystemEntries(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void A_save_that_cannot_be_written_leaves_the_old_token_whole()
    {
        // Written in place, a save cut short left a truncated blob that decrypted to nothing and
        // signed the user out. A folder where the new copy goes stands in for the disk giving out.
        var store = Store();
        store.SetToken("kept");
        Directory.CreateDirectory(Path.Combine(_directory, "token.bin.tmp"));

        Assert.ThrowsAny<SystemException>(() => store.SetToken("lost"));

        Assert.Equal("kept", store.GetToken());
        Assert.Equal("kept", Store().GetToken());
    }

    [Fact]
    public void A_token_file_another_program_has_open_is_stored_but_out_of_reach_and_says_why_once()
    {
        // Read as no token, it opened an account with a cache read-only, behind a notice that
        // Todoist had turned the token away.
        Store().SetToken("tok");
        var starting = Store();

        using (Hold())
        {
            Assert.Equal(new TokenRead(null, Unreachable: true), starting.ReadToken());
            Assert.Equal(new TokenRead(null, Unreachable: true), starting.ReadToken());
        }

        Assert.Equal(new TokenRead("tok"), starting.ReadToken());
        Assert.Single(_log.Lines, l => l.StartsWith("WARN The saved token is open in another program", StringComparison.Ordinal));
        Assert.Contains("IOException", _log.All);
    }

    [Fact]
    public void A_token_once_read_is_still_had_while_another_program_holds_the_file()
    {
        // Read on every sync, a scanner opening the file mid-session held syncing up.
        Store().SetToken("tok");
        var store = Store();
        Assert.Equal("tok", store.GetToken());

        using (Hold())
        {
            Assert.Equal(new TokenRead("tok"), store.ReadToken());
        }
    }

    [Fact]
    public void No_token_file_is_no_token()
    {
        Assert.Equal(new TokenRead(null), Store().ReadToken());

        // Nor is a config folder that isn't there yet, as on a first run.
        Assert.Equal(new TokenRead(null), new DpapiSecretStore(new Paths(Path.Combine(_directory, "missing"))).ReadToken());
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void A_token_file_this_user_cant_decrypt_is_no_token()
    {
        // Copied from another account, or left by a reset profile: it'll never be readable, so
        // asking for a new token is right, where waiting for this one would wait for ever.
        File.WriteAllBytes(TokenFile, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Equal(new TokenRead(null), Store().ReadToken());
        Assert.Contains("WARN The saved token can't be decrypted", _log.All);
    }

    [Fact]
    public void A_token_file_that_can_never_be_read_is_no_token_rather_than_one_to_wait_for()
    {
        // Access refused, here by a folder where the file should be. Nothing lets go of it, so
        // waiting for it waited for ever, and never asked for a token that would.
        Directory.CreateDirectory(TokenFile);

        Assert.Equal(new TokenRead(null), Store().ReadToken());
        Assert.Contains("WARN The saved token can't be read, so a new one will be asked for.", _log.All);
    }

    [Fact]
    public async Task A_token_saved_or_cleared_while_the_file_is_held_for_a_moment_goes_through()
    {
        Store().SetToken("old");

        var held = Hold();
        var saving = Task.Run(() => Store().SetToken("new"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await held.DisposeAsync();
        await saving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("new", Store().GetToken());

        held = Hold();
        var clearing = Task.Run(() => Store().ClearToken(), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await held.DisposeAsync();
        await clearing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(TokenFile));
    }

    [Fact]
    public void A_token_cleared_while_the_file_wont_be_let_go_is_gone_for_the_session()
    {
        // Signing out reports it, since the token's still on the disk for the next launch; but a
        // token Todoist turned away mustn't be sent again meanwhile.
        Store().SetToken("rejected");
        var store = Store();
        Assert.Equal("rejected", store.GetToken());

        using (Hold(FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(store.ClearToken);
        }

        Assert.Equal(new TokenRead(null), store.ReadToken());
        Assert.Equal("rejected", Store().GetToken());
    }

    private sealed class Paths(string config) : IAppPaths
    {
        public string ConfigDirectory => config;

        public string CacheDirectory => config;

        public string LogDirectory => config;

        public string AttachmentDirectory => config;

        public string DownloadsDirectory => config;
    }
}
