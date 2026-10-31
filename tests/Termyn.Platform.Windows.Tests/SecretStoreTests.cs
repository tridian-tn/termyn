using Termyn.Core.Platform;
using Termyn.Platform.Windows;

namespace Termyn.Platform.Windows.Tests;

/// <summary>The token's file, and what a save that goes wrong part-way leaves in it.</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "termyn-secret-tests", Guid.NewGuid().ToString("N"));

    public SecretStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DpapiSecretStore Store() => new(new Paths(_directory));

    [Fact]
    public void A_token_saved_over_another_is_the_one_read_back()
    {
        var store = Store();
        store.SetToken("first");
        store.SetToken("second");

        Assert.Equal("second", store.GetToken());
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
