namespace Termyn.Platform.Windows;

/// <summary>
/// Asks which font families this machine has, for the places that prefer one and fall back.
/// </summary>
public static class InstalledFonts
{
    /// <summary>Whether a font family of that name is on this machine.</summary>
    /// <param name="family">The family's name, such as "Segoe UI"</param>
    /// <returns>True when a family answers to that name</returns>
    public static bool Has(string family)
    {
        try
        {
            using var found = new FontFamily(family);
            return true;
        }
        catch (ArgumentException)
        {
            // What a name nothing answers to gives back, which is the question being asked.
            return false;
        }
    }
}
