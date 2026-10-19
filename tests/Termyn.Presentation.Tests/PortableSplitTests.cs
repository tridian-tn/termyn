using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Termyn.Core;

namespace Termyn.Presentation.Tests;

/// <summary>
/// That Core and Presentation stay something a client on another OS could build on.
/// </summary>
/// <remarks>
/// Targeting <c>net10.0</c> rather than <c>net10.0-windows</c> does most of this already: Windows
/// Forms can't be referenced at all, and CA1416 fails the build on an API marked as Windows-only.
/// These check what that lets through. <c>System.Drawing.Color</c> is in every runtime, so it
/// compiles here; so does a P/Invoke into user32; and <c>[SupportedOSPlatform("windows")]</c> on a
/// type tells CA1416 that everything in it is fine.
///
/// What none of them catches is portable logic written in App.Windows, because it references
/// nothing it shouldn't. That's what the read-through in docs/portable-split.md is for.
/// </remarks>
public class PortableSplitTests
{
    public static TheoryData<string> Projects => ["Termyn.Core", "Termyn.Presentation"];

    [Theory]
    [MemberData(nameof(Projects))]
    public void It_is_built_for_every_operating_system(string project)
    {
        // net10.0-windows stamps the assembly with both of these. Written by hand on a type or a
        // member, either says that code runs on one OS only, which also stops CA1416 asking.
        var marked = Read(AssemblyOf(project), reader => reader.CustomAttributes
            .Select(reader.GetCustomAttribute)
            .Where(a => AttributeName(reader, a) is "System.Runtime.Versioning.SupportedOSPlatformAttribute"
                                                 or "System.Runtime.Versioning.TargetPlatformAttribute")
            .Select(a => $"{OwnerOf(reader, a.Parent)} ({PlatformOf(reader, a)})")
            .Distinct()
            .ToList());

        Assert.True(
            marked.Count == 0,
            $"{project} is meant to run on any OS, but these are marked for one: " + string.Join(", ", marked));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void It_builds_only_on_the_projects_below_it(string project)
    {
        // Today the framework target keeps the Windows projects out, and Core can't take Presentation
        // without a cycle. A project for another OS would target net10.0 too, though, and nothing but
        // this would stop either of these reaching into it.
        string[] below = project == "Termyn.Presentation" ? ["Termyn.Core"] : [];

        var above = AssemblyOf(project)
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Termyn.", StringComparison.Ordinal))
            .Except(below)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            above.Count == 0,
            $"{project} builds on {string.Join(", ", above)}. Core builds on nothing of Termyn's and "
            + "Presentation only on Core, so that another client can take them without the rest");
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

            var loaded = reader.TypeReferences
                .Select(reader.GetTypeReference)
                .Where(t => reader.GetString(t.Namespace) == "System.Runtime.InteropServices"
                         && reader.GetString(t.Name) == "NativeLibrary")
                .Select(_ => "NativeLibrary");

            var imported = reader.TypeDefinitions
                .Where(h => reader.GetTypeDefinition(h).Attributes.HasFlag(TypeAttributes.Import))
                .Select(h => $"{TypeName(reader, h)} (COM)");

            return invoked.Concat(loaded).Concat(imported).ToList();
        });

        Assert.True(
            native.Count == 0,
            $"{project} reaches native code through {string.Join(", ", native)}");
    }

    private static Assembly AssemblyOf(string project) => project switch
    {
        "Termyn.Core" => typeof(Links).Assembly,
        "Termyn.Presentation" => typeof(SyncStatus).Assembly,
        _ => throw new ArgumentOutOfRangeException(nameof(project), project, null),
    };

    /// <summary>
    /// Reads an assembly's metadata as the compiler wrote it.
    /// </summary>
    /// <remarks>
    /// The metadata lists every type the assembly names from another one, wherever it's named: a
    /// signature, a local, a call. Reflection only sees signatures, which is how a type used inside
    /// a method body would get past it.
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

    private static bool Within(string ns, string root)
        => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

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
    /// The full name of an attribute's type, when it comes from another assembly.
    /// </summary>
    /// <param name="reader">The metadata the attribute is in</param>
    /// <param name="attribute">The attribute as applied</param>
    /// <returns>The type's namespace and name, or nothing for one declared in the same assembly</returns>
    private static string AttributeName(MetadataReader reader, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference) return "";

        var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        if (parent.Kind != HandleKind.TypeReference) return "";

        var type = reader.GetTypeReference((TypeReferenceHandle)parent);
        return $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";
    }

    /// <summary>
    /// The platform an OS attribute names, which both of them take as their only argument.
    /// </summary>
    /// <param name="reader">The metadata the attribute is in</param>
    /// <param name="attribute">A SupportedOSPlatform or TargetPlatform attribute</param>
    /// <returns>The platform, such as <c>windows</c> or <c>Windows7.0</c></returns>
    private static string PlatformOf(MetadataReader reader, CustomAttribute attribute)
    {
        // The value starts with a two-byte prolog, then the arguments in order.
        var value = reader.GetBlobReader(attribute.Value);
        value.ReadUInt16();
        return value.ReadSerializedString() ?? "";
    }

    private static string OwnerOf(MetadataReader reader, EntityHandle owner) => owner.Kind switch
    {
        HandleKind.AssemblyDefinition => "the assembly",
        HandleKind.TypeDefinition => TypeName(reader, (TypeDefinitionHandle)owner),
        HandleKind.MethodDefinition => MemberName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)owner)),
        HandleKind.PropertyDefinition => reader.GetString(reader.GetPropertyDefinition((PropertyDefinitionHandle)owner).Name),
        _ => $"a {owner.Kind}",
    };

    private static string MemberName(MetadataReader reader, MethodDefinition method)
        => $"{TypeName(reader, method.GetDeclaringType())}.{reader.GetString(method.Name)}";
}
