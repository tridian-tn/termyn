namespace Termyn.Platform.Mac.Tests;

/// <summary>
/// That the app's folders are where a Mac app's should be. Under a library made for the test, since
/// asking for the real one makes the folders in it.
/// </summary>
public sealed class MacAppPathsTests : IDisposable
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly string _library = Path.Combine(Path.GetTempPath(), "termyn-paths-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_library))
            Directory.Delete(_library, recursive: true);
    }

    [Fact]
    public void The_settings_are_in_Application_Support()
        => Assert.Equal(Path.Combine(_library, "Application Support", "Termyn"), new MacAppPaths(_library).ConfigDirectory);

    [Fact]
    public void The_cache_stays_out_of_Caches_because_it_holds_the_changes_not_yet_sent()
    {
        var paths = new MacAppPaths(_library);

        Assert.Equal(paths.ConfigDirectory, paths.CacheDirectory);
        Assert.False(paths.CacheDirectory.StartsWith(Path.Combine(_library, "Caches"), StringComparison.Ordinal));
    }

    [Fact]
    public void The_logs_are_where_Console_looks_for_them()
        => Assert.Equal(Path.Combine(_library, "Logs", "Termyn"), new MacAppPaths(_library).LogDirectory);

    [Fact]
    public void The_settings_and_logs_folders_are_there_once_asked_for()
    {
        var paths = new MacAppPaths(_library);

        Assert.True(Directory.Exists(paths.ConfigDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
    }

    [Fact]
    public void Attachments_are_in_Caches_and_not_made_until_one_is_opened()
    {
        var paths = new MacAppPaths(_library);

        Assert.Equal(Path.Combine(_library, "Caches", "Termyn", "attachments"), paths.AttachmentDirectory);
        Assert.False(Directory.Exists(Path.Combine(_library, "Caches")));
    }

    [Fact]
    public void The_library_is_the_users_own_rather_than_the_Unix_idea_of_one()
    {
        // ~/.config is what .NET's own special folders answer with on macOS.
        Assert.Equal(Path.Combine(Home, "Library"), MacAppPaths.UserLibrary);
    }

    [Fact]
    public void Downloads_are_the_users_own_folder()
        => Assert.Equal(Path.Combine(Home, "Downloads"), new MacAppPaths(_library).DownloadsDirectory);
}
