using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Termyn.Core.Model;
using Termyn.Core.Settings;

namespace Termyn.TestSupport;

/// <summary>
/// Text the app says to something other than a person, which no catalogue may hold.
/// </summary>
/// <remarks>
/// A catalogue is what a translation replaces. Everything here is compared against exactly as
/// written — by Todoist, by SQLite, by the settings file — so one of these moved into a catalogue
/// goes on working in English and breaks the day anybody translates it, on a machine no test run
/// here is set up like.
///
/// Two checks, because a lot of it looks like prose. "today" is a word of the filter grammar, and
/// "day" is one the reminder list says to people; telling those apart by looking at them can't
/// be done. So the code that speaks protocol is held to never reading a catalogue at all, which
/// catches a keyword moved out of it however it's worded — and the catalogues are held to having
/// nothing that couldn't be prose, wherever it's used from.
/// </remarks>
public static partial class ProtocolText
{
    /// <summary>
    /// Text that's protocol on sight: the grammar's own tokens, and SQLite's English.
    /// </summary>
    /// <remarks>
    /// Plain words of the grammar — "today", "every", "monday", "no date" — aren't here, since the
    /// app can say the same words to a person. They're left to <see cref="CatalogueReadsIn"/>.
    /// Words are compared case and all.
    /// </remarks>
    private static readonly HashSet<string> Words =
    [
        ":to_me:", ":to_others:", "od", "p1", "p2", "p3", "p4",
        "database disk image is malformed", "file is not a database", "unable to open database file",
        "database is locked",

        // Taken from the code rather than copied: the colours and resource types are Todoist's own
        // names for them, and the settings keys are whatever the settings file is written with.
        .. TodoistPalette.Names,
        .. ResourceType.All,
        .. SettingsKeys(typeof(AppSettings), typeof(ViewState)),
    ];

    /// <summary>
    /// Whether some text is protocol rather than prose.
    /// </summary>
    /// <remarks>
    /// The text above, or anything shaped like something written for a machine: a filter query
    /// (a keyword and its colon, lower-case, anywhere — "search: {0}"); snake_case, the way the sync
    /// protocol names its fields and commands; camelCase, the way the settings file names its keys;
    /// an ISO date format; or SQL. Taken trimmed, since a value in a .resx keeps whatever space was
    /// typed round it.
    /// </remarks>
    /// <param name="text">A catalogue entry's text</param>
    /// <returns>Whether it's protocol</returns>
    public static bool IsProtocol(string text)
    {
        var trimmed = text.Trim();

        return Words.Contains(trimmed)
            || Query().IsMatch(trimmed)
            || MachineName().IsMatch(trimmed)
            || trimmed.StartsWith("yyyy-MM-dd", StringComparison.Ordinal)
            || Sql().IsMatch(trimmed);
    }

    /// <summary>
    /// The entries in an assembly's catalogues that are protocol rather than prose.
    /// </summary>
    /// <remarks>
    /// Every embedded resource file, not only the one called Strings: a form's own .resx carries its
    /// captions too. Read off what was built rather than out of the source tree, and a build with no
    /// resources at all throws rather than reading as one with nothing wrong. The designer's
    /// bookkeeping — the entries whose keys start <c>&gt;&gt;</c>, naming controls and their types —
    /// isn't text anyone reads, so it's left out.
    /// </remarks>
    /// <param name="assembly">The assembly whose catalogues to read</param>
    /// <returns>Each entry that's protocol, as the resource it's in, its key and its text</returns>
    public static IReadOnlyList<string> InCataloguesOf(Assembly assembly)
    {
        var embedded = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".resources", StringComparison.Ordinal))
            .ToList();

        if (embedded.Count == 0)
            throw new MissingManifestResourceException($"{assembly.GetName().Name} has no catalogue");

        var found = new List<string>();
        foreach (var name in embedded)
        {
            using var reader = new ResourceReader(assembly.GetManifestResourceStream(name)!);

            var entries = reader.GetEnumerator();
            while (entries.MoveNext())
            {
                if (entries.Key is not string key || key.StartsWith(">>", StringComparison.Ordinal))
                    continue;

                // Asked what it is before it's read: a form's images would have to be deserialised
                // to look at, and they aren't text.
                reader.GetResourceData(key, out var kind, out _);
                if (kind == "ResourceTypeCode.String" && entries.Value is string text && IsProtocol(text))
                    found.Add($"{name}: {key} = \"{text}\"");
            }
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The places a type reads a catalogue, which for code that speaks protocol should be none.
    /// </summary>
    /// <remarks>
    /// Read off the compiled code, its lambdas, local functions and state machines included, for
    /// any use of a generated catalogue class or of a resource manager directly. That's the failure
    /// itself, rather than what the moved text looks like: a keyword the parser now compares against
    /// a catalogue lookup.
    /// </remarks>
    /// <param name="type">The type to read</param>
    /// <returns>Each method that reads one, with what it reads</returns>
    public static IReadOnlyList<string> CatalogueReadsIn(Type type)
    {
        var reads = new List<string>();

        foreach (var declaring in WithNested(type))
        {
            const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var method in declaring.GetMethods(all).Cast<MethodBase>().Concat(declaring.GetConstructors(all)))
                foreach (var used in MembersUsedBy(method))
                    if (IsCatalogue(used as Type ?? used.DeclaringType))
                        reads.Add($"{declaring.Name}.{method.Name} reads {(used as Type ?? used.DeclaringType)!.Name}.{used.Name}");
        }

        return reads;
    }

    /// <summary>A type and every type declared inside it, compiler-generated ones included.</summary>
    private static IEnumerable<Type> WithNested(Type type)
        => [type, .. type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNested)];

    /// <summary>
    /// Whether a type is a catalogue: the class generated for a .resx, which hands out its resource
    /// manager, or the resource machinery itself.
    /// </summary>
    private static bool IsCatalogue(Type? type)
        => type is not null
        && (type == typeof(ResourceManager)
            || type.GetProperty("ResourceManager", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.PropertyType == typeof(ResourceManager));

    /// <summary>The methods, fields and types a method's compiled code refers to.</summary>
    private static IEnumerable<MemberInfo> MembersUsedBy(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            yield break;

        var typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (var at = 0; at < il.Length;)
        {
            var code = il[at] == 0xFE ? OpCodesByValue[(short)(0xFE00 | il[at + 1])] : OpCodesByValue[il[at]];
            at += code.Size;

            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
                yield return method.Module.ResolveMember(BitConverter.ToInt32(il, at), typeArgs, methodArgs)!;

            at += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
                _ => 4,
            };
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(c => c.Value);

    /// <summary>
    /// The keys the settings file is written with, as its serialiser names them. Not the properties
    /// it's told to leave out, which are worked out from the others and never reach the file.
    /// </summary>
    private static IEnumerable<string> SettingsKeys(params Type[] types)
        => types
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name));

    /// <summary>A filter query's keyword and its colon, lower-case, anywhere in the text.</summary>
    [GeneratedRegex(@"(^|[\s(!&|,])(search|due|date|deadline|created|workspace|assigned to|assigned by|added by)( before| after)?:")]
    private static partial Regex Query();

    /// <summary>
    /// snake_case, or camelCase starting with a word of two letters or more and going on in whole
    /// capitalised words — which is what a serialiser makes of a property name, and isn't what
    /// "iCal" or "macOS" are.
    /// </summary>
    [GeneratedRegex("^([a-z][a-z0-9]*(_[a-z0-9]+)+|[a-z]{2,}[a-z0-9]*([A-Z][a-z0-9]+)+)$")]
    private static partial Regex MachineName();

    /// <summary>
    /// SQL: a statement's first keyword in capitals, or in lower case with the clause that makes it
    /// a statement after it — "Select a project" is neither. The clause can be on a line of its own.
    /// </summary>
    [GeneratedRegex(@"^(SELECT|INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|PRAGMA|BEGIN|COMMIT)\b|^(select|insert|update|delete|create|drop|alter|pragma)\s.*\b(from|into|set|table|index|where|values)\b", RegexOptions.Singleline)]
    private static partial Regex Sql();
}
