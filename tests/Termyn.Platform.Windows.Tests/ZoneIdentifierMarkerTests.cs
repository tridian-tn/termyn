using Termyn.Platform.Windows;

namespace Termyn.Platform.Windows.Tests;

/// <summary>The Mark of the Web on a downloaded attachment.</summary>
public sealed class ZoneIdentifierMarkerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "termyn-zone-tests", Guid.NewGuid().ToString("N"));

    public ZoneIdentifierMarkerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void A_marked_file_says_it_came_from_the_internet_and_from_where()
    {
        var file = Path.Combine(_directory, "invoice.pdf");
        File.WriteAllBytes(file, [1, 2, 3]);

        new ZoneIdentifierMarker().Mark(file, "https://files.todoist.com/abc/invoice.pdf");

        var mark = File.ReadAllText(file + ":Zone.Identifier");
        Assert.Contains("[ZoneTransfer]", mark, StringComparison.Ordinal);
        Assert.Contains("ZoneId=3", mark, StringComparison.Ordinal);
        Assert.Contains("HostUrl=https://files.todoist.com/abc/invoice.pdf", mark, StringComparison.Ordinal);

        // The file itself is untouched; the mark is beside it.
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\server\share\invoice.pdf")]
    [InlineData("not a url")]
    public void Only_a_web_address_is_recorded_as_where_it_came_from(string? source)
    {
        var mark = ZoneIdentifierMarker.Contents(source);

        Assert.Contains("ZoneId=3", mark, StringComparison.Ordinal);
        Assert.DoesNotContain("HostUrl", mark, StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_in_the_address_are_not_recorded()
    {
        var mark = ZoneIdentifierMarker.Contents("https://user:secret@files.example/a.pdf");

        Assert.Contains("HostUrl=https://files.example/a.pdf", mark, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", mark, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_cannot_be_marked_is_left_as_it_is_without_a_fuss()
    {
        // Standing in for a volume with no alternate streams: there's no file to mark at all.
        new ZoneIdentifierMarker().Mark(Path.Combine(_directory, "missing", "gone.pdf"), "https://files.example/a");
    }
}
