using System.Runtime.CompilerServices;

namespace Termyn.TestSupport;

/// <summary>
/// Where the repository is, for the tests that read what's committed rather than what's built.
/// </summary>
public static class Repository
{
    /// <summary>
    /// The folder holding the solution.
    /// </summary>
    /// <remarks>
    /// Found from this file as it was compiled, so the tests can run from a build put anywhere, and
    /// from the test binary if the compiler was told to hide where the sources were. Keyed on the
    /// solution file rather than Directory.Build.props, which also sits in tests/.
    /// </remarks>
    /// <param name="source">This file's path, filled in by the compiler</param>
    /// <returns>The folder with <c>Termyn.slnx</c> in it</returns>
    public static string Root([CallerFilePath] string source = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(source), AppContext.BaseDirectory })
        {
            for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Termyn.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("couldn't find Termyn.slnx above the tests or their sources");
    }
}
