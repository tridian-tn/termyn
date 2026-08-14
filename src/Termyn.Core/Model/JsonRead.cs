using System.Text.Json.Nodes;

namespace Termyn.Core.Model;

/// <summary>
/// Tolerant readers for values off a raw resource object. Todoist has represented flags as both
/// JSON booleans and 0/1 integers, and ids as both strings and numbers, so every read here accepts
/// either shape rather than silently yielding a default.
/// </summary>
internal static class JsonRead
{
    public static string? String(JsonObject o, string key)
        => o.TryGetPropertyValue(key, out var n) && n is JsonValue v ? v.ToString() : null;

    public static int Int(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v)
            return 0;
        if (v.TryGetValue(out int i))
            return i;
        return int.TryParse(v.ToString(), out var parsed) ? parsed : 0;
    }

    /// <summary>
    /// A whole number that may not fit in an int — a file size, which Todoist has sent as both a
    /// JSON number and a string.
    /// </summary>
    public static long Long(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v)
            return 0;
        if (v.TryGetValue(out long l))
            return l;
        return long.TryParse(v.ToString(), out var parsed) ? parsed : 0;
    }

    public static bool Bool(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v)
            return false;
        if (v.TryGetValue(out bool b))
            return b;
        if (v.TryGetValue(out int i))
            return i != 0;
        return bool.TryParse(v.ToString(), out var parsed) && parsed;
    }
}
