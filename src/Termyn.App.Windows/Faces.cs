namespace Termyn.App.Windows;

/// <summary>
/// The typefaces the description panel draws with.
/// </summary>
/// <remarks>
/// The one place saying what code is set in. The editor and the rendered view both name the face in
/// the document they build, and the two are drawing the same description — so they have to agree
/// about it, and pointing somewhere else one day should be a single edit rather than two that can be
/// made separately.
///
/// Held as a name because a name is all a rich text document wants.
/// <see cref="FontFamily.GenericMonospace"/> hands back a new family, with a GDI+ handle behind it,
/// on every read — so it's read once here.
/// </remarks>
internal static class Faces
{
    /// <summary>What code is set in, wherever it's drawn.</summary>
    internal static readonly string FixedWidth = ReadFixedWidth();

    private static string ReadFixedWidth()
    {
        using var family = FontFamily.GenericMonospace;
        return family.Name;
    }
}
