namespace Termyn.App.Windows;

/// <summary>
/// The typefaces the description panel draws with, looked up once.
/// </summary>
/// <remarks>
/// <see cref="FontFamily.GenericMonospace"/> hands back a new family — and a new GDI+ handle behind
/// it — on every read, and both halves of the panel ask for it on a hot path: the editor on every
/// pause in the typing, the rendered view on every run of code it draws. Each of those handles is
/// then abandoned, so the finaliser thread is deleting them while the two halves are asking GDI+
/// for the same family. Held here instead, so the face is resolved once and both halves draw code
/// in the same one.
/// </remarks>
internal static class Faces
{
    /// <summary>What code is set in, wherever it is drawn.</summary>
    internal static readonly FontFamily FixedWidth = FontFamily.GenericMonospace;
}
