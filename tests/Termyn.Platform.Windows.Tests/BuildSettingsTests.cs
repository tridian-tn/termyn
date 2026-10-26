using System.Xml.Linq;
using Termyn.TestSupport;

namespace Termyn.Platform.Windows.Tests;

/// <summary>
/// That every project in the solution fails its build on a warning.
/// </summary>
/// <remarks>
/// A project that only warns still builds and passes CI, so nobody sees the warnings, and the test
/// projects had built up over four hundred of them that way. The projects are read from the
/// solution rather than listed here, so one added later is held to it without anyone editing this.
/// </remarks>
public class BuildSettingsTests
{
    public static TheoryData<string> Projects()
    {
        var solution = XDocument.Load(Path.Combine(Repository.Root(), "Termyn.slnx"));
        return new(solution.Descendants("Project").Select(p => (string)p.Attribute("Path")!));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Warnings_fail_the_build(string path)
    {
        var root = Repository.Root();
        var project = Path.Combine(root, path);
        Assert.True(File.Exists(project), $"the solution names {path}, which isn't there");

        // Everywhere MSBuild could take the setting from: the project, and each Directory.Build
        // file between it and the repo. A .targets file is read after the project, so it'd have the
        // last word on anything set earlier.
        var sources = new[] { project }
            .Concat(Ancestors(Path.GetDirectoryName(project)!, root)
                .SelectMany(d => new[] { "Directory.Build.props", "Directory.Build.targets" }.Select(f => Path.Combine(d, f))))
            .Where(File.Exists);

        var settings = sources
            .SelectMany(f => XDocument.Load(f).Descendants("TreatWarningsAsErrors").Select(e => (File: Path.GetRelativePath(root, f), Setting: e)))
            .ToList();

        // Unconditional, or it could be on for one configuration or project and quietly off for the
        // rest: tests/Directory.Build.props already has a group that leaves TestSupport out.
        Assert.True(
            settings.Any(s => IsTrue(s.Setting) && Unconditional(s.Setting)),
            $"nothing turns TreatWarningsAsErrors on for every build of {path}");

        var otherwise = settings.Where(s => !IsTrue(s.Setting)).Select(s => s.File).ToList();
        Assert.True(otherwise.Count == 0, $"TreatWarningsAsErrors is set to something else for {path} in {string.Join(", ", otherwise)}");

        // Turned off, the props above that set it are never read at all.
        var skipsProps = XDocument.Load(project).Descendants("ImportDirectoryBuildProps").Any(e => e.Value.Trim() != "true");
        Assert.False(skipsProps, $"{path} doesn't import Directory.Build.props");
    }

    /// <summary>
    /// Whether a property is set to true, however it's spaced or cased.
    /// </summary>
    /// <param name="setting">The property's element</param>
    /// <returns>True when its value is true</returns>
    private static bool IsTrue(XElement setting)
        => string.Equals(setting.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a property applies to every build, which it doesn't if it or any group holding it
    /// has a condition.
    /// </summary>
    /// <param name="setting">The property's element</param>
    /// <returns>True when nothing it sits in has a condition</returns>
    private static bool Unconditional(XElement setting)
        => setting.AncestorsAndSelf().All(e => e.Attribute("Condition") is null);

    /// <summary>
    /// The directories from one up to the repo, both included.
    /// </summary>
    /// <param name="from">The directory to start at</param>
    /// <param name="root">The repo, where the walk stops</param>
    /// <returns>The directories, innermost first</returns>
    private static IEnumerable<string> Ancestors(string from, string root)
    {
        for (var directory = new DirectoryInfo(from); directory is not null; directory = directory.Parent)
        {
            yield return directory.FullName;
            if (string.Equals(directory.FullName.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                yield break;
        }
    }
}
