using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Termyn.Platform.Windows.Tests;

/// <summary>
/// Holds the notices file to what the app actually ships, and ships it with the app.
/// </summary>
/// <remarks>
/// What ships is read from the app's restore graph rather than from a list kept here. Three of the
/// five packages arrive underneath Microsoft.Data.Sqlite rather than being asked for, so a list
/// written by hand is exactly the thing that would miss the next one. A package counts when it puts
/// a file in the output; one that only pulls others in has nothing of its own to credit.
/// </remarks>
public class ThirdPartyNoticesTests
{
    private static readonly string Root = BrandIconTests.RepoRoot();

    private static readonly string Notices = File.ReadAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.txt"));

    /// <summary>A package that puts a file in the app's output.</summary>
    /// <param name="Id">Its NuGet id</param>
    /// <param name="Folder">Where restore unpacked it</param>
    private sealed record Shipped(string Id, string Folder)
    {
        /// <summary>The element of its nuspec that says what licence it's under.</summary>
        public XElement Licence
            => XDocument.Load(Path.Combine(Folder, Id.ToLowerInvariant() + ".nuspec"))
                .Descendants()
                .Single(e => e.Name.LocalName == "license");
    }

    /// <summary>One component's part of the notices.</summary>
    /// <param name="Packages">The package ids it covers</param>
    /// <param name="Licence">The licence it names in its heading</param>
    /// <param name="Text">The whole section, heading and licence text together</param>
    private sealed record Section(IReadOnlyList<string> Packages, string Licence, string Text);

    [Fact]
    public void Every_package_the_app_ships_has_a_notice()
    {
        var noticed = Sections().SelectMany(s => s.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(Packages(), p => !noticed.Contains(p.Id));
    }

    [Fact]
    public void Nothing_has_a_notice_the_app_no_longer_ships()
    {
        // The other direction, so a dependency taken out doesn't leave a licence behind for
        // something that isn't there.
        var shipped = Packages().Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(Sections().SelectMany(s => s.Packages), id => !shipped.Contains(id));
    }

    [Fact]
    public void Each_notice_is_for_the_licence_its_package_declares()
    {
        // Checked against the package that restore actually fetched, so an upgrade that changes a
        // licence fails here rather than shipping under the old one's text.
        var wrong = new List<string>();

        foreach (var package in Packages())
        {
            var section = Sections().Single(s => s.Packages.Contains(package.Id, StringComparer.OrdinalIgnoreCase));
            var declared = package.Licence;

            if ((string?)declared.Attribute("type") == "expression")
            {
                if (declared.Value != section.Licence)
                    wrong.Add($"{package.Id} declares {declared.Value}; the notice says {section.Licence}");
            }
            else
            {
                // A licence shipped as a file has no name to compare, so its text has to be there.
                var text = File.ReadAllText(Path.Combine(package.Folder, declared.Value));
                if (!Flat(section.Text).Contains(Flat(text), StringComparison.Ordinal))
                    wrong.Add($"{package.Id}'s {declared.Value} isn't in its notice");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void The_notices_go_wherever_the_app_goes()
    {
        // Into the publish directory, beside Termyn.exe rather than in a folder of their own...
        var project = XDocument.Load(Path.Combine(Root, "src", "Termyn.App.Windows", "Termyn.App.Windows.csproj"));
        var item = Assert.Single(
            project.Descendants(),
            e => ((string?)e.Attribute("Include"))?.EndsWith("THIRD-PARTY-NOTICES.txt", StringComparison.Ordinal) == true);

        Assert.Equal(
            Path.Combine(Root, "THIRD-PARTY-NOTICES.txt"),
            Path.GetFullPath(Path.Combine(Root, "src", "Termyn.App.Windows", (string)item.Attribute("Include")!)));
        Assert.Equal("THIRD-PARTY-NOTICES.txt", (string?)item.Attribute("Link"));
        Assert.Contains((string?)item.Attribute("CopyToPublishDirectory"), new[] { "PreserveNewest", "Always" });

        // ...which is the whole of what the installer and the portable zip are made from.
        var script = File.ReadAllText(Path.Combine(Root, "packaging", "Termyn.iss"));
        var build = File.ReadAllText(Path.Combine(Root, "packaging", "build.ps1"));

        Assert.Matches(@"Source: ""\{#PublishDir\}\\\*""; DestDir: ""\{app\}""", script);
        Assert.Contains("CreateFromDirectory($publish, $zip)", build);
    }

    /// <summary>The packages that put a file in the app's output, read from its restore graph.</summary>
    private static IReadOnlyList<Shipped> Packages()
    {
        var assetsPath = Path.Combine(Root, "src", "Termyn.App.Windows", "obj", "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"The app hasn't been restored in place, so there's nothing to read at {assetsPath}");

        var assets = JsonNode.Parse(File.ReadAllText(assetsPath))!;
        var folders = assets["packageFolders"]!.AsObject().Select(f => f.Key).ToList();

        // The runtime's own target, which is what publish copies from.
        var target = assets["targets"]!.AsObject().Single(t => t.Key.EndsWith("/win-x64", StringComparison.Ordinal)).Value!;

        var shipped = new List<Shipped>();
        foreach (var (key, entry) in target.AsObject())
        {
            if ((string?)entry!["type"] != "package")
                continue;

            var files = new[] { "runtime", "native", "runtimeTargets" }
                .SelectMany(kind => entry[kind]?.AsObject().Select(f => f.Key) ?? [])
                .Where(f => !f.EndsWith("_._", StringComparison.Ordinal));

            if (!files.Any())
                continue;

            var path = (string)assets["libraries"]![key]!["path"]!;
            var folder = folders.Select(f => Path.Combine(f, path)).First(Directory.Exists);
            shipped.Add(new Shipped(key[..key.IndexOf('/')], folder));
        }

        return shipped;
    }

    /// <summary>The notices, one section per component.</summary>
    private static IReadOnlyList<Section> Sections()
    {
        var headings = Regex.Matches(Notices, @"^Packages: (.+)\r?\nLicence: +(.+?)\r?$", RegexOptions.Multiline);

        return headings
            .Select((h, i) => new Section(
                h.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                h.Groups[2].Value,
                Notices[h.Index..(i + 1 < headings.Count ? headings[i + 1].Index : Notices.Length)]))
            .ToList();
    }

    /// <summary>Text with every run of whitespace made one space, so wrapping and line endings don't count.</summary>
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
