using System.Globalization;
using System.Text;

namespace Termyn.Core.Attachments;

/// <summary>
/// Whether a file is a kind that runs code when it's opened, so the window can ask before handing it
/// to the desktop.
/// </summary>
/// <remarks>
/// An attachment's name is whatever a collaborator called it, and opening a file goes through the
/// shell, which runs a program as readily as it shows a document. "invoice.scr" one click from
/// running is the case this exists for.
///
/// One list for every platform rather than one each: a name means the same thing whichever desktop
/// it lands on, and a Windows script sitting in a Mac's cache is harmless to ask about. It covers
/// programs, scripts, installers, shortcuts that point at either, and disk images, which open onto
/// whatever is inside them. Documents that can carry macros are left to the program that opens
/// them, which asks about a file marked as downloaded.
/// </remarks>
public static class FileRisk
{
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows: programs, and what the shell loads as one.
        "exe", "com", "scr", "pif", "cpl", "msc", "gadget",

        // Windows: installers and packages.
        "msi", "msp", "mst", "msix", "msixbundle", "appx", "appxbundle", "appinstaller",
        "application", "appref-ms", "xbap", "vsix", "diagcab",

        // Windows: scripts and the hosts that run them.
        "bat", "cmd", "vb", "vbs", "vbe", "vbscript", "js", "jse", "ws", "wsf", "wsh", "wsc", "sct",
        "hta", "ps1", "ps1xml", "ps2", "ps2xml", "psc1", "psc2", "psd1", "psm1", "pssc", "cdxml",
        "msh", "msh1", "msh2", "mshxml", "msh1xml", "msh2xml",

        // Windows: shortcuts, settings and shell files that point at something else to run.
        "lnk", "url", "scf", "reg", "settingcontent-ms", "search-ms", "library-ms", "shb", "shs",
        "rdp", "wsb", "theme", "themepack", "desktopthemepackfile", "chm", "hlp", "mcf", "xnk",

        // Windows: Office and Access files that are code rather than documents.
        "xll", "xla", "xlam", "ppa", "ppam", "ade", "adp", "mda", "mde", "accde",

        // macOS: applications, installers, scripts, and the files that open a link or a command.
        "app", "pkg", "mpkg", "command", "tool", "terminal", "workflow", "action", "scpt", "scptd",
        "applescript", "osax", "kext", "prefpane", "plugin", "webloc", "inetloc", "fileloc",
        "mobileconfig", "dmg",

        // Linux desktops.
        "desktop", "appimage", "run", "deb", "rpm", "snap", "flatpakref",

        // Disk images, which open onto whatever is inside them.
        "iso", "img", "vhd", "vhdx",

        // Everywhere: scripts any desktop may hand to an interpreter, and Java archives.
        "sh", "bash", "zsh", "csh", "ksh", "tcsh", "py", "pyw", "pyz", "pyzw", "pyc", "pl", "rb",
        "jar", "jnlp",
    };

    /// <summary>
    /// Whether a file of this name runs code, or starts something that does, when it's opened.
    /// </summary>
    /// <remarks>
    /// Judged by the name the way the desktop would read it: invisible format characters can't
    /// hide the real extension, and trailing dots and spaces, which Windows drops, can't push it
    /// out of reach.
    /// </remarks>
    /// <param name="fileName">A file's name, or a path to it</param>
    /// <returns>True when the desktop would run something rather than show it</returns>
    public static bool RunsCode(string fileName)
    {
        var name = Visible(fileName).TrimEnd('/', '\\');
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..].TrimEnd('.', ' ');

        var dot = name.LastIndexOf('.');
        return dot >= 0 && Runnable.Contains(name[(dot + 1)..]);
    }

    /// <summary>
    /// A name without the characters that change how it reads but not what it is.
    /// </summary>
    /// <remarks>
    /// Format characters (Unicode's Cf), the bidirectional overrides among them: a U+202E after
    /// "invoice" turns "invoicefdp.exe" into something that reads as a PDF. None of them is anything
    /// a person would put in a name on purpose.
    /// </remarks>
    /// <param name="name">A name from an account</param>
    /// <returns>The same name with those characters taken out</returns>
    internal static string Visible(string name)
    {
        var kept = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
                kept.Append(rune.ToString());

        return kept.ToString();
    }
}
