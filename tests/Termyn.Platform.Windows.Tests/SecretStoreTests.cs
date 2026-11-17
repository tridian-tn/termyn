using Termyn.Core.Api;
using Termyn.Core.Platform;
using Termyn.Core.Sync;
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

        var saving = LetGoOnceTriedAgain(() => Store().SetToken("new"), "saving the token");
        await saving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("new", Store().GetToken());

        var clearing = LetGoOnceTriedAgain(() => Store().ClearToken(), "clearing the token");
        await clearing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(TokenFile));
    }

    [Fact]
    public void A_token_forgotten_while_the_file_is_held_isnt_waited_for()
    {
        // For a token Todoist turned away, forgotten holding the lock the window reads through. Waiting
        // out the file there froze the window for half a second.
        Store().SetToken("rejected");
        var store = Store();
        Assert.Equal("rejected", store.GetToken());

        using (Hold(FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(store.ForgetToken);
        }

        Assert.DoesNotContain("being tried again", _log.All);
        Assert.Equal(new TokenRead(null), store.ReadToken());
    }

    [Fact]
    public async Task A_rejected_token_whose_file_is_held_still_puts_the_account_on_hold_straight_away()
    {
        // The engine and the real store together: the delete that can't happen is written down
        // rather than waited for, and isn't reported in place of the rejection.
        Store().SetToken("tok");
        var store = Store();
        var cache = new InMemorySnapshotStore();
        cache.PutResource("items", "i1", """{"id":"i1","content":"Private"}""");
        var engine = new SyncEngine(new FakeApi { Throw = new TodoistAuthException("rejected") }, cache, store, log: _log);
        engine.Load();

        using (Hold(FileShare.Read))
        {
            await Assert.ThrowsAsync<TodoistAuthException>(() => engine.SyncAsync(TestContext.Current.CancellationToken));
        }

        Assert.True(engine.AwaitingToken);
        Assert.Contains("WARN The rejected token couldn't be deleted", _log.All);
        Assert.DoesNotContain("being tried again", _log.All);
        Assert.Equal(new TokenRead(null), store.ReadToken());
    }

    /// <summary>
    /// Holds the file, starts something on it, and lets go as soon as the store says it's trying
    /// again, which shows it met the file held rather than starting after it was let go.
    /// </summary>
    /// <remarks>
    /// Waits on this thread rather than after an await, so a busy thread pool can't hold up the
    /// letting go past the half-second the store keeps trying for.
    /// </remarks>
    /// <param name="io">What to do to the file meanwhile</param>
    /// <param name="what">What the store calls it in the log</param>
    /// <returns>The thing being done, to wait for</returns>
    private Task LetGoOnceTriedAgain(Action io, string what)
    {
        using var held = Hold();
        var doing = Task.Run(io, TestContext.Current.CancellationToken);

        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!_log.All.Contains($"so {what} is being tried again", StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < giveUp, $"The store never met the file held while {what}.");
            Thread.Sleep(5);
        }

        return doing;
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
