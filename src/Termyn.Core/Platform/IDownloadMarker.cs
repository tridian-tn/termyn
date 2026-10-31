namespace Termyn.Core.Platform;

/// <summary>
/// Tags a downloaded file as having come from the internet, the way a browser tags what it saves.
/// </summary>
/// <remarks>
/// What lets the desktop treat an attachment with the suspicion it gives any download: Windows
/// warns before running a marked program and Office opens a marked document in Protected View;
/// macOS has Gatekeeper check a marked application before it first runs. Without it a collaborator's
/// file is one click from running as though it had been on this machine all along.
/// <para>
/// On Windows this writes the <c>Zone.Identifier</c> alternate data stream with the Internet zone.
/// A macOS implementation should set the <c>com.apple.quarantine</c> extended attribute (flags,
/// a hex timestamp, the agent name "Termyn" and a fresh UUID, as LaunchServices writes it).
/// </para>
/// <para>
/// Marking is a courtesy on top of a download that has already succeeded, so an implementation
/// never throws: a file system that can't carry the mark (FAT32 and exFAT have no alternate
/// streams) leaves the file as it is, logged at most.
/// </para>
/// </remarks>
public interface IDownloadMarker
{
    /// <summary>Marks a file just written as downloaded.</summary>
    /// <param name="path">The file, finished and closed</param>
    /// <param name="sourceUrl">Where it came from, recorded only when it's a web address</param>
    void Mark(string path, string? sourceUrl);
}

/// <summary>Marks nothing: for tests, and for a platform with no such mark.</summary>
public sealed class NoDownloadMarker : IDownloadMarker
{
    /// <summary>The one there needs to be.</summary>
    public static readonly NoDownloadMarker Instance = new();

    private NoDownloadMarker()
    {
    }

    public void Mark(string path, string? sourceUrl)
    {
    }
}
