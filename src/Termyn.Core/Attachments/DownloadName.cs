namespace Termyn.Core.Attachments;

/// <summary>
/// The name a saved copy of an attachment goes by: the one it has on the account, as near as
/// Windows will take it.
/// </summary>
/// <remarks>
/// A name from an account isn't one this machine has to accept as a path. A separator in it would
/// put the copy somewhere other than the folder it was saved to, so every character Windows refuses
/// in a name becomes an underscore. Nothing else is touched, since keeping the name is the point.
/// </remarks>
internal static class DownloadName
{
    /// <summary>What a name with nothing usable left in it is saved as.</summary>
    private const string Fallback = "attachment";

    /// <summary>
    /// What Windows refuses in a file name, besides the control characters.
    /// </summary>
    /// <remarks>
    /// Written out rather than asked of <see cref="Path.GetInvalidFileNameChars"/>, which answers for
    /// the OS it's running on, and anywhere but Windows that's only the slash and the nul.
    /// </remarks>
    private const string Refused = "<>:\"/\\|?*";

    /// <summary>
    /// Names that open a device rather than a file, with an extension or without: "nul.txt" is NUL.
    /// </summary>
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// The account's name for a file, made into one Windows will create as given.
    /// </summary>
    /// <param name="fileName">The name on the account</param>
    /// <returns>The same name with anything Windows refuses replaced</returns>
    public static string Safe(string fileName)
    {
        var chars = fileName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] < ' ' || Refused.Contains(chars[i]))
                chars[i] = '_';

        // Windows drops these from the end of a name by itself, so "notes." would be made as "notes",
        // which is a different name from the one just found to be free.
        var name = new string(chars).TrimEnd('.', ' ');
        if (name.Length == 0)
            return Fallback;

        return Devices.Contains(name.Split('.')[0].TrimEnd(' ')) ? "_" + name : name;
    }

    /// <summary>
    /// The name to try once the plain one's taken, numbered the way a browser numbers a second
    /// download of the same thing.
    /// </summary>
    /// <param name="name">A name already made safe</param>
    /// <param name="number">How many forms of it have been found taken, zero for the plain one</param>
    /// <returns>The name with the number before its extension, or the name itself for zero</returns>
    public static string Numbered(string name, int number)
    {
        if (number == 0)
            return name;

        var extension = Path.GetExtension(name);

        // A name that's all extension, like ".gitignore", is numbered at the end: putting the number
        // in front would turn it into some other kind of file.
        if (extension.Length == name.Length)
            extension = string.Empty;

        return $"{name[..^extension.Length]} ({number}){extension}";
    }
}
