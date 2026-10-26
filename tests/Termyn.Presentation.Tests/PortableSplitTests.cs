using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Termyn.Core;
using Termyn.TestSupport;

namespace Termyn.Presentation.Tests;

/// <summary>
/// That Core and Presentation stay something a client on another OS could build on.
/// </summary>
/// <remarks>
/// Targeting <c>net10.0</c> rather than <c>net10.0-windows</c> does most of this already: Windows
/// Forms can't be referenced at all, and CA1416 fails the build on an API marked as Windows-only.
/// These check what that lets through. <c>System.Drawing.Color</c> is in every runtime, so it
/// compiles here; so does a P/Invoke into user32; and an <c>OperatingSystem.IsWindows()</c> check
/// round a call, or <c>[SupportedOSPlatform("windows")]</c> on a type, tells CA1416 that it's fine.
///
/// What none of them catches is portable logic written in App.Windows, because it references
/// nothing it shouldn't. That's what the read-through in docs/portable-split.md is for.
/// </remarks>
public class PortableSplitTests
{
    public static TheoryData<string> Projects => ["Termyn.Core", "Termyn.Presentation"];

    // net10.0-windows stamps the assembly with the first two. Written by hand on a type or a member,
    // any of them says that code is for some OSes and not others, and the guards tell CA1416 so.
    private static readonly HashSet<string> OSMarkers =
    [
        "System.Runtime.Versioning.TargetPlatformAttribute",
        "System.Runtime.Versioning.SupportedOSPlatformAttribute",
        "System.Runtime.Versioning.UnsupportedOSPlatformAttribute",
        "System.Runtime.Versioning.ObsoletedOSPlatformAttribute",
        "System.Runtime.Versioning.SupportedOSPlatformGuardAttribute",
        "System.Runtime.Versioning.UnsupportedOSPlatformGuardAttribute",
    ];

    // Each a way out to native code without a P/Invoke of the assembly's own: loading a library by
    // hand, and the source-generated COM interop, whose types aren't marked as COM imports.
    private static readonly HashSet<string> Interop =
    [
        "System.Runtime.InteropServices.NativeLibrary",
        "System.Runtime.InteropServices.ComWrappers",
        "System.Runtime.InteropServices.Marshalling.StrategyBasedComWrappers",
        "System.Runtime.InteropServices.Marshalling.GeneratedComInterfaceAttribute",
        "System.Runtime.InteropServices.Marshalling.GeneratedComClassAttribute",
    ];

    // Anything in a project or build file that would let a Windows-only call through CA1416. A
    // category or global severity of warning or error is fine; anything lower lets it pass.
    private static readonly Regex Quietens = new(
        @"CA1416"
        + @"|<(EnableNETAnalyzers|RunAnalyzers|RunAnalyzersDuringBuild|TreatWarningsAsErrors|CodeAnalysisTreatWarningsAsErrors)>\s*false"
        + @"|<(AnalysisMode|AnalysisModeInteroperability)>\s*None"
        + @"|<(AnalysisLevel|AnalysisLevelInteroperability)>\s*none"
        + @"|dotnet_analyzer_diagnostic\.(category-Interoperability\.)?severity\s*=\s*(none|silent|suggestion)",
        RegexOptions.IgnoreCase);

    // The assemblies every runtime has. A reference assembly's name, such as System.Runtime, is
    // the name of one of these, so what's here is the framework whatever it's called.
    private static readonly HashSet<string> Framework = Directory
        .EnumerateFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")
        .Select(f => Path.GetFileNameWithoutExtension(f))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(Projects))]
    public void It_is_built_for_every_operating_system(string project)
    {
        var marked = Read(AssemblyOf(project), reader => reader.CustomAttributes
            .Select(reader.GetCustomAttribute)
            .Where(a => OSMarkers.Contains(AttributeName(reader, a)))
            .Select(a => $"{OwnerOf(reader, a.Parent)} ({PlatformOf(reader, a)})")
            .Distinct()
            .ToList());

        Assert.True(
            marked.Count == 0,
            $"{project} is meant to run on any OS, but these are marked for some: " + string.Join(", ", marked));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void It_builds_only_on_what_every_OS_has(string project)
    {
        // The framework is the same everywhere. Past it, a package has to be known to run on every
        // OS before it's listed here, so adding one is a decision rather than a side effect: a
        // wrapper round user32 reaches native code without a P/Invoke in this assembly at all.
        //
        // Termyn's own projects are listed the same way. Today the framework target keeps the
        // Windows ones out, and Core can't take Presentation without a cycle, but a Platform project
        // for another OS would target net10.0 too.
        string[] beyond = project == "Termyn.Presentation" ? ["Termyn.Core", "Markdig"] : ["Microsoft.Data.Sqlite"];

        var unknown = AssemblyOf(project)
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !Framework.Contains(n))
            .Except(beyond)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unknown.Count == 0,
            $"{project} builds on {string.Join(", ", unknown)}. Core builds on nothing of Termyn's and "
            + "Presentation only on Core, and a package goes on the list here once it's known to run "
            + "on every OS");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void It_names_no_type_from_the_Windows_side(string project)
    {
        var named = Read(AssemblyOf(project), reader => reader.TypeReferences
            .Select(reader.GetTypeReference)
            .Select(t => (Namespace: reader.GetString(t.Namespace), Name: reader.GetString(t.Name)))
            .Where(t => IsWindowsSide(t.Namespace))
            .Select(t => $"{t.Namespace}.{t.Name}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList());

        Assert.True(
            named.Count == 0,
            $"{project} names {string.Join(", ", named)}, which belong on the Windows side, in "
            + "Platform.Windows or App.Windows. A colour down here is Core's Rgb");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void It_never_asks_which_OS_it_is_on(string project)
    {
        // A check like this round a call is all CA1416 wants, so whatever's behind it, the build
        // is happy. Code that needs to know is code for one OS, and belongs in a Platform project.
        var asked = Read(AssemblyOf(project), reader => reader.MemberReferences
            .Select(reader.GetMemberReference)
            .Select(m => (Type: TypeRefName(reader, m.Parent), Name: reader.GetString(m.Name)))
            .Where(m => (m.Type == "System.OperatingSystem" && m.Name.StartsWith("Is", StringComparison.Ordinal))
                     || (m.Type == "System.Runtime.InteropServices.RuntimeInformation" && m.Name == "IsOSPlatform"))
            .Select(m => $"{m.Type}.{m.Name}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList());

        Assert.True(
            asked.Count == 0,
            $"{project} asks {string.Join(", ", asked)}, which a portable project has no need to");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void It_calls_no_native_code(string project)
    {
        // A P/Invoke compiles under any target, and fails only when it runs somewhere its library
        // isn't. Native code belongs in a Platform project, or in a package that ships it for every
        // OS, the way SQLite comes.
        var native = Read(AssemblyOf(project), reader =>
        {
            var invoked = reader.MethodDefinitions
                .Select(reader.GetMethodDefinition)
                .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
                .Select(m => $"{MemberName(reader, m)} "
                           + $"({reader.GetString(reader.GetModuleReference(m.GetImport().Module).Name)})");

            var interop = reader.TypeReferences
                .Select(h => TypeRefName(reader, h))
                .Where(Interop.Contains);

            var imported = reader.TypeDefinitions
                .Where(h => reader.GetTypeDefinition(h).Attributes.HasFlag(TypeAttributes.Import))
                .Select(h => $"{TypeName(reader, h)} (COM)");

            return invoked.Concat(interop).Concat(imported).ToList();
        });

        Assert.True(
            native.Count == 0,
            $"{project} reaches native code through {string.Join(", ", native)}");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void CA1416_still_fails_the_build(string project)
    {
        // Every check above leans on CA1416 for whatever Windows-only API they don't list, and none
        // of the ways to quieten it leaves anything in the assembly to find: a pragma, NoWarn, an
        // .editorconfig severity for it or for its whole category, an analysis mode of None, or
        // analysers or warnings-as-errors turned off. Those last can be set in any folder from the
        // project up to the repo, and a Directory.Build.targets there has the last word.
        var root = Repository.Root();
        var directory = Path.Combine(root, "src", project);

        var inProject = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(Path.GetRelativePath(directory, f), @"^(bin|obj)[\\/]"))
            .Where(f => Path.GetExtension(f) is ".cs" or ".csproj" or ".props" or ".targets" or ".editorconfig" or ".globalconfig");

        var above = Ancestors(Path.GetDirectoryName(directory)!, root)
            .SelectMany(d => new[] { "Directory.Build.props", "Directory.Build.targets", ".editorconfig", ".globalconfig" }
                .Select(f => Path.Combine(d, f)))
            .Where(File.Exists);

        var silencing = inProject.Concat(above)
            .Where(f => Quietens.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .Order(StringComparer.Ordinal)
            .ToList();

        var projectFile = File.ReadAllText(Path.Combine(directory, $"{project}.csproj"));

        Assert.True(
            silencing.Count == 0,
            $"CA1416 is quietened for {project} in {string.Join(", ", silencing)}");
        Assert.True(
            Regex.IsMatch(projectFile, @"<TreatWarningsAsErrors>\s*true\s*</TreatWarningsAsErrors>"),
            $"{project} no longer treats warnings as errors, so CA1416 only warns");
    }

    /// <summary>
    /// The assembly a portable project builds into, as loaded for these tests.
    /// </summary>
    /// <param name="project">The project's name, which is its assembly's too</param>
    /// <returns>The assembly, checked to be the one named</returns>
    private static Assembly AssemblyOf(string project)
    {
        var assembly = project switch
        {
            "Termyn.Core" => typeof(Links).Assembly,
            "Termyn.Presentation" => typeof(SyncStatus).Assembly,
            _ => throw new ArgumentOutOfRangeException(nameof(project), project, null),
        };

        // The type standing in for each is only a way to reach its assembly. Moved to the other
        // project, it'd have one checked twice and this one never.
        Assert.Equal(project, assembly.GetName().Name);
        return assembly;
    }

    /// <summary>
    /// A folder and each one above it, up to and including the repo's.
    /// </summary>
    /// <param name="from">The folder to start at, inside the repo</param>
    /// <param name="root">The repo's folder, where it stops</param>
    /// <returns>The folders, nearest first</returns>
    private static IEnumerable<string> Ancestors(string from, string root)
    {
        for (var directory = new DirectoryInfo(from); directory is not null; directory = directory.Parent)
        {
            yield return directory.FullName;
            if (Path.GetRelativePath(root, directory.FullName) == ".") yield break;
        }
    }

    /// <summary>
    /// Reads an assembly's metadata as the compiler wrote it.
    /// </summary>
    /// <remarks>
    /// The metadata lists every type and member the assembly names from another one, wherever it's
    /// named: a signature, a local, a call. Reflection only sees signatures, which is how a type
    /// used inside a method body would get past it.
    /// </remarks>
    /// <param name="assembly">The assembly whose file to read</param>
    /// <param name="read">What to take from it, finished before the file is closed</param>
    /// <returns>Whatever <paramref name="read"/> took</returns>
    private static T Read<T>(Assembly assembly, Func<MetadataReader, T> read)
    {
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        return read(pe.GetMetadataReader());
    }

    /// <summary>
    /// Whether a namespace holds Windows Forms, the types it draws with, or the registry.
    /// </summary>
    /// <remarks>
    /// A nested type is listed with no namespace of its own, but the type it's nested in is listed
    /// as well, so it's caught there. Microsoft.Win32 counts but what's under it doesn't:
    /// Microsoft.Win32.SafeHandles is in every runtime.
    /// </remarks>
    /// <param name="ns">The namespace of a type the assembly names</param>
    /// <returns>True if a portable project has no business naming it</returns>
    private static bool IsWindowsSide(string ns)
        => Within(ns, "System.Windows.Forms") || Within(ns, "System.Drawing") || ns == "Microsoft.Win32";

    /// <summary>
    /// Whether a namespace is another or one of those under it.
    /// </summary>
    /// <param name="ns">The namespace to test</param>
    /// <param name="root">The namespace it might be in</param>
    /// <returns>True for <paramref name="root"/> itself or anything under it</returns>
    private static bool Within(string ns, string root)
        => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

    /// <summary>
    /// The full name of a type the assembly declares, with any it's nested in.
    /// </summary>
    /// <param name="reader">The metadata the type is in</param>
    /// <param name="handle">The type</param>
    /// <returns>The name, nested types after a <c>+</c></returns>
    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var outer = type.GetDeclaringType();

        if (!outer.IsNil) return $"{TypeName(reader, outer)}+{name}";

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    /// <summary>
    /// The full name of a type named from another assembly.
    /// </summary>
    /// <param name="reader">The metadata the name is in</param>
    /// <param name="handle">What names the type, such as a member reference's parent</param>
    /// <returns>The type's namespace and name, or nothing when the handle isn't a type reference</returns>
    private static string TypeRefName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeReference) return "";

        var type = reader.GetTypeReference((TypeReferenceHandle)handle);
        return $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";
    }

    /// <summary>
    /// The full name of an attribute's type, when it comes from another assembly.
    /// </summary>
    /// <param name="reader">The metadata the attribute is in</param>
    /// <param name="attribute">The attribute as applied</param>
    /// <returns>The type's namespace and name, or nothing for one declared in the same assembly</returns>
    private static string AttributeName(MetadataReader reader, CustomAttribute attribute)
        => attribute.Constructor.Kind == HandleKind.MemberReference
            ? TypeRefName(reader, reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent)
            : "";

    /// <summary>
    /// The platform an OS attribute names, which each of them takes as its first argument.
    /// </summary>
    /// <param name="reader">The metadata the attribute is in</param>
    /// <param name="attribute">One of the attributes in <see cref="OSMarkers"/></param>
    /// <returns>The platform, such as <c>windows</c> or <c>Windows7.0</c></returns>
    private static string PlatformOf(MetadataReader reader, CustomAttribute attribute)
    {
        // The value starts with a two-byte prolog, then the arguments in order.
        var value = reader.GetBlobReader(attribute.Value);
        value.ReadUInt16();
        return value.ReadSerializedString() ?? "";
    }

    /// <summary>
    /// What an attribute is applied to, in words for a failure message.
    /// </summary>
    /// <param name="reader">The metadata the attribute is in</param>
    /// <param name="owner">The attribute's parent</param>
    /// <returns>The assembly, or the type or member's name</returns>
    private static string OwnerOf(MetadataReader reader, EntityHandle owner) => owner.Kind switch
    {
        HandleKind.AssemblyDefinition => "the assembly",
        HandleKind.TypeDefinition => TypeName(reader, (TypeDefinitionHandle)owner),
        HandleKind.MethodDefinition => MemberName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)owner)),
        HandleKind.PropertyDefinition => reader.GetString(reader.GetPropertyDefinition((PropertyDefinitionHandle)owner).Name),
        _ => $"a {owner.Kind}",
    };

    /// <summary>
    /// A method's name, after the type that declares it.
    /// </summary>
    /// <param name="reader">The metadata the method is in</param>
    /// <param name="method">The method</param>
    /// <returns>The type's full name and the method's</returns>
    private static string MemberName(MetadataReader reader, MethodDefinition method)
        => $"{TypeName(reader, method.GetDeclaringType())}.{reader.GetString(method.Name)}";
}
