namespace Termyn.App.Windows;

/// <summary>
/// The typefaces the description panel draws with, looked up once.
/// </summary>
/// <remarks>
/// <see cref="FontFamily.GenericMonospace"/> hands back a new family — and a new GDI+ handle behind
/// it — on every read; it caches nothing. <see cref="MarkdownView"/> asks for one on every run of
/// code it draws, so a description of any size leaves a handful of handles behind for the finaliser
/// to collect. That is the same waste the view keeps its own cache of fonts to avoid, and it is
/// avoided here for the cost of one field.
///
/// The other half of it is that this is the one place saying what code is set in. The editor names
/// the face in the RTF it builds and the view sets it on a selection, and the two are drawing the
/// same description — so they have to agree about it, and pointing somewhere else one day should be
/// a single edit rather than two that can be made separately.
///
/// It is worth saying what this is not for, since it was written under a theory that turned out to
/// be wrong: it has nothing to do with the tests that fail in clusters on CI. Those were the caret
/// not going where it was put, and no arrangement of font lookups was ever going to touch them.
/// </remarks>
internal static class Faces
{
    /// <summary>What code is set in, wherever it is drawn.</summary>
    internal static readonly FontFamily FixedWidth = FontFamily.GenericMonospace;
}
