using System.Text;
using Termyn.Core.Logging;
using Termyn.Core.Platform;

namespace Termyn.Platform.Windows;

/// <summary>
/// Marks a download with the Mark of the Web: a <c>Zone.Identifier</c> stream naming the Internet
/// zone, as a browser writes it.
/// </summary>
/// <remarks>
/// Read by SmartScreen and the Attachment Manager before a program runs, and by Office, which opens
/// a marked document in Protected View. The stream rides along with the file when it's copied
/// elsewhere on NTFS, so the mark goes wherever the user takes it.
/// </remarks>
public sealed class ZoneIdentifierMarker : IDownloadMarker
{
    private readonly ILog? _log;

    /// <param name="log">Told when a file couldn't be marked; null to say nothing</param>
    public ZoneIdentifierMarker(ILog? log = null) => _log = log;

    /// <inheritdoc />
    public void Mark(string path, string? sourceUrl)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", Contents(sourceUrl), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A volume with no alternate streams — FAT32, exFAT, some network shares — has nowhere
            // to put it. The file is there all the same, which is what was asked for.
            _log?.Info($"A download couldn't be marked as coming from the internet: {ex.Message}");
        }
    }

    /// <summary>What goes in the stream.</summary>
    /// <remarks>
    /// The address is written only when it's a web one, without any credentials it carries: it's
    /// for whoever looks at the file's properties to see where it came from, and the Attachment
    /// Manager reads the zone alone.
    /// </remarks>
    /// <param name="sourceUrl">Where the file came from</param>
    internal static string Contents(string? sourceUrl)
    {
        var text = new StringBuilder();
        text.Append("[ZoneTransfer]\r\n");

        // The Internet zone, which is what a download from a server is.
        text.Append("ZoneId=3\r\n");

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            var bare = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;
            text.Append($"HostUrl={bare.AbsoluteUri}\r\n");
        }

        return text.ToString();
    }
}
