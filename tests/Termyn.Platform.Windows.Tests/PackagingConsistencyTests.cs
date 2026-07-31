using System.Text.RegularExpressions;
using Microsoft.Win32;
using Termyn.Core.Platform;
using Termyn.Platform.Windows;

namespace Termyn.Platform.Windows.Tests;

/// <summary>
/// Holds the installer and the app to the same facts.
/// </summary>
/// <remarks>
/// The Pascal in the .iss can't be unit-tested, but it's a text file naming the same registry entry
/// and the same directories the app uses — and when the two disagree the symptoms are silent. A
/// mismatched value name leaves two startup entries the settings screen can't see; a mismatched
/// directory makes "remove my data" delete nothing while telling the user it did.
/// </remarks>
public class PackagingConsistencyTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(BrandIconTests.RepoRoot(), "packaging", "Termyn.iss"));

    [Fact]
    public void The_installer_writes_the_startup_entry_the_app_writes()
    {
        var registry = Match(@"Root: HKCU; Subkey: ""([^""]+)""[\s\S]*?ValueName: ""([^""]+)""; ValueData: ""(.+?)"";");
        var subkey = registry.Groups[1].Value;
        var valueName = registry.Groups[2].Value;

        // Inno doubles a quote to escape it inside a quoted string.
        var valueData = registry.Groups[3].Value.Replace("\"\"", "\"");

        Assert.Equal(WindowsAutoStart.RunKey, subkey);
        Assert.Equal(WindowsAutoStart.ValueName, valueName);

        // What the app would write for the same install location, character for character.
        const string installed = @"C:\Users\someone\AppData\Local\Programs\Termyn";
        var expected = valueData
            .Replace("{app}", installed)
            .Replace("{#AppExe}", "Termyn.exe");

        var key = $@"Software\Termyn.Tests\{Guid.NewGuid():N}";
        try
        {
            new WindowsAutoStart($@"{installed}\Termyn.exe", key).SetEnabled(true);
            using var written = Registry.CurrentUser.OpenSubKey(key);
            Assert.Equal(expected, written!.GetValue(WindowsAutoStart.ValueName));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Termyn.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void The_uninstaller_removes_the_startup_entry_whoever_wrote_it()
    {
        // The app writes the same entry from its settings screen, so removal can't be conditional on
        // the installer's own task having been ticked.
        Assert.Matches(
            @"RegDeleteValue\(HKEY_CURRENT_USER,\s*'Software\\Microsoft\\Windows\\CurrentVersion\\Run',\s*'Termyn'\)",
            Script);
    }

    [Fact]
    public void The_uninstaller_deletes_the_directories_the_app_actually_uses()
    {
        var paths = new WindowsAppPaths();

        // The Pascal names them with Inno constants; expand those the way Inno would.
        var deleted = Regex.Matches(Script, @"DelTree\(ExpandConstant\('([^']+)'\)")
            .Select(m => m.Groups[1].Value
                .Replace("{userappdata}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
                .Replace("{localappdata}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            .ToList();

        Assert.Contains(paths.ConfigDirectory, deleted);
        Assert.Contains(paths.CacheDirectory, deleted);
    }

    [Fact]
    public void The_installer_launches_the_executable_the_build_produces()
    {
        var csproj = File.ReadAllText(Path.Combine(
            BrandIconTests.RepoRoot(), "src", "Termyn.App.Windows", "Termyn.App.Windows.csproj"));

        var assemblyName = Match(@"<AssemblyName>([^<]+)</AssemblyName>", csproj).Groups[1].Value;

        Assert.Equal(assemblyName + ".exe", Match(@"#define AppExe ""([^""]+)""").Groups[1].Value);
    }

    [Fact]
    public void The_installer_and_the_executable_carry_the_same_mark()
    {
        var csproj = File.ReadAllText(Path.Combine(
            BrandIconTests.RepoRoot(), "src", "Termyn.App.Windows", "Termyn.App.Windows.csproj"));

        var appIcon = Match(@"<ApplicationIcon>([^<]+)</ApplicationIcon>", csproj).Groups[1].Value;
        var setupIcon = Match(@"SetupIconFile=(\S+)").Groups[1].Value;

        Assert.EndsWith(@"assets\termyn.ico", appIcon, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"assets\termyn.ico", setupIcon, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_installer_stays_per_user()
    {
        // The property the whole no-elevation install rests on, and the one most easily lost to a
        // stray edit.
        Assert.Contains("PrivilegesRequired=lowest", Script);
        // Empty on purpose: with no override allowed it can't be talked into a machine-wide install.
        Assert.Matches(@"(?m)^PrivilegesRequiredOverridesAllowed=\s*$", Script.Replace("\r\n", "\n"));
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\", Script);
        Assert.DoesNotContain("Root: HKLM", Script);
    }

    [Fact]
    public void The_version_is_never_defaulted_by_the_installer_script()
    {
        // A fallback here would be a second home for the version, and compiling the script directly
        // would then produce an installer confidently mislabelled.
        Assert.Contains("#error AppVersion must be passed in", Script);
    }

    [Fact]
    public void The_runtime_the_installer_looks_for_is_the_one_the_app_targets()
    {
        var csproj = File.ReadAllText(Path.Combine(
            BrandIconTests.RepoRoot(), "src", "Termyn.App.Windows", "Termyn.App.Windows.csproj"));

        // net10.0-windows → the 10.0 desktop runtime channel.
        var target = Match(@"<TargetFramework>net([\d.]+)-windows</TargetFramework>", csproj).Groups[1].Value;

        Assert.Equal(target, Match(@"#define DotnetChannel ""([^""]+)""").Groups[1].Value);
    }

    [Fact]
    public void The_installer_looks_where_the_architecture_it_publishes_keeps_its_runtime()
    {
        var csproj = File.ReadAllText(Path.Combine(
            BrandIconTests.RepoRoot(), "src", "Termyn.App.Windows", "Termyn.App.Windows.csproj"));

        // The roots below are the ones this RID in particular needs; a change here wants them revisited.
        Assert.Equal("win-x64", Match(@"<RuntimeIdentifier>([^<]+)</RuntimeIdentifier>", csproj).Groups[1].Value);

        var roots = Regex.Matches(Script, @"Roots\[\d+\] := ExpandConstant\('([^']+)'\)")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // An Arm64 machine keeps the Arm64 runtime in the plain dotnet directory and the x64 one
        // alongside it under dotnet\x64. A win-x64 apphost needs the latter, so leaving that root
        // out tells an Arm64 user their runtime is missing while it sits right there.
        Assert.Contains(roots, r => r.Contains(@"\dotnet\x64\shared\", StringComparison.OrdinalIgnoreCase));

        // And both roots have to be under Program Files: without DOTNET_ROOT set that's where the
        // apphost looks, so a runtime found anywhere else wouldn't be one it could load.
        Assert.All(roots, r => Assert.StartsWith("{commonpf64}", r, StringComparison.Ordinal));
    }

    private static Match Match(string pattern, string? text = null)
    {
        var match = Regex.Match(text ?? Script, pattern);
        Assert.True(match.Success, $"nothing matched /{pattern}/");
        return match;
    }
}
