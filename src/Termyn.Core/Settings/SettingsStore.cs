using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Termyn.Core.Platform;

namespace Termyn.Core.Settings;

/// <summary>
/// Reads and writes <c>config.json</c>. Keys the running version doesn't know are carried through a
/// save untouched, on the same reasoning as the resource cache: a file written by a later Termyn
/// must survive being opened by an earlier one.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // The file is documented as hand-editable, and the default encoder writes the hotkey — the
        // field most likely to be edited — as "Ctrl+Alt+A".
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    /// <summary>The file as last read, so a save can be an overlay rather than a replacement.</summary>
    private JsonObject _raw = new();

    /// <summary>
    /// The version the file claimed, kept apart from the settings record. A save must not read it
    /// back off a record that may have been defaulted, or a later version's file gets stamped down.
    /// </summary>
    private int _version = AppSettings.CurrentSchemaVersion;

    /// <summary>False when a file exists but couldn't be read, which makes saving unsafe.</summary>
    private bool _readable = true;

    public SettingsStore(IAppPaths paths)
        : this(System.IO.Path.Combine(paths.ConfigDirectory, "config.json"))
    {
    }

    public SettingsStore(string path) => FilePath = path;

    public string FilePath { get; }

    /// <summary>
    /// Reads the settings, falling back to defaults when there is no file or it can't be read. An
    /// unreadable file is moved aside rather than overwritten, so whatever was in it is recoverable.
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            string text;
            try
            {
                if (!File.Exists(FilePath))
                    return Reset();

                text = File.ReadAllText(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or unreadable — busy, most likely. Run on defaults for this session and
                // refuse to save, because an overlay onto nothing would write those defaults over a
                // file whose real contents we never managed to read.
                _readable = false;
                return Reset();
            }

            JsonObject root;
            try
            {
                root = JsonNode.Parse(text) as JsonObject
                       ?? throw new JsonException("config.json is not a JSON object.");
            }
            catch (JsonException)
            {
                SetAside();
                return Reset();
            }

            Migrate(root);
            _raw = root;
            _readable = true;
            _version = Math.Max(ReadVersion(root), AppSettings.CurrentSchemaVersion);
            return Read(root);
        }
    }

    /// <summary>Writes the settings, preserving any keys this version doesn't model.</summary>
    /// <returns>False when the file could not be written, so the caller can say so.</returns>
    public bool Save(AppSettings settings)
    {
        lock (_gate)
        {
            // Whatever is on disk is real and we couldn't read it. Overlaying defaults onto an empty
            // object would replace every one of the user's settings with a default.
            if (!_readable)
                return false;

            var known = JsonSerializer.SerializeToNode(settings, Format) as JsonObject ?? new JsonObject();

            // Never lower the version, and take it from the file rather than the record: a record
            // that fell back to defaults carries our version, not the file's.
            known["schemaVersion"] = Math.Max(_version, AppSettings.CurrentSchemaVersion);

            var merged = _raw.DeepClone().AsObject();
            Overlay(merged, known);

            var temp = FilePath + ".tmp";
            try
            {
                var directory = System.IO.Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                // Written beside the target and moved into place, so a crash mid-write can't leave a
                // half-written config that the next start would treat as corrupt.
                File.WriteAllText(temp, merged.ToJsonString(Format));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Delete(temp);
                return false;
            }

            _raw = merged;
            return true;
        }
    }

    /// <summary>Forgets the file and answers with defaults.</summary>
    private AppSettings Reset()
    {
        _raw = new JsonObject();
        _version = AppSettings.CurrentSchemaVersion;
        return new AppSettings();
    }

    /// <summary>
    /// Reads the settings a field at a time.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>Deserialize&lt;AppSettings&gt;</c>, which is all-or-nothing: one bad value —
    /// a hand-edited typo, or an enum name a later version added — discarded every other setting in
    /// the file, and the save that followed wrote those defaults through. A bad field now costs that
    /// field alone.
    /// </remarks>
    private static AppSettings Read(JsonObject root)
    {
        var defaults = new AppSettings();
        return new AppSettings
        {
            SchemaVersion = Int(root, "schemaVersion", defaults.SchemaVersion),
            Hotkey = Text(root, "hotkey", defaults.Hotkey),
            HotkeyEnabled = Flag(root, "hotkeyEnabled", defaults.HotkeyEnabled),
            Theme = Choice(root, "theme", defaults.Theme),
            SyncMode = Choice(root, "syncMode", defaults.SyncMode),
            SyncIntervalSeconds = Int(root, "syncIntervalSeconds", defaults.SyncIntervalSeconds),
            LaunchAtLogin = Flag(root, "launchAtLogin", defaults.LaunchAtLogin),
            CloseToTray = Flag(root, "closeToTray", defaults.CloseToTray),
            View = ReadView(root["view"] as JsonObject),
        };
    }

    private static ViewState ReadView(JsonObject? view)
    {
        var defaults = new ViewState();
        if (view is null)
            return defaults;

        return new ViewState
        {
            SelectedKey = view["selectedKey"] is JsonValue key && key.TryGetValue(out string? s) ? s : defaults.SelectedKey,
            CollapsedKeys = view["collapsedKeys"] is JsonArray keys
                ? keys.OfType<JsonValue>().Select(k => k.ToString()).ToList()
                : defaults.CollapsedKeys,
            SidebarWidth = Int(view, "sidebarWidth", defaults.SidebarWidth),
            WindowX = Nullable(view, "windowX"),
            WindowY = Nullable(view, "windowY"),
            WindowWidth = Int(view, "windowWidth", defaults.WindowWidth),
            WindowHeight = Int(view, "windowHeight", defaults.WindowHeight),
            Maximized = Flag(view, "maximized", defaults.Maximized),
        };
    }

    private static string Text(JsonObject o, string key, string fallback)
        => o[key] is JsonValue v && v.TryGetValue(out string? s) && s is not null ? s : fallback;

    private static bool Flag(JsonObject o, string key, bool fallback)
        => o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    private static int Int(JsonObject o, string key, int fallback)
        => o[key] is JsonValue v && v.TryGetValue(out int i) ? i : fallback;

    private static int? Nullable(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    private static T Choice<T>(JsonObject o, string key, T fallback) where T : struct, Enum
        => o[key] is JsonValue v && v.TryGetValue(out string? s) && Enum.TryParse<T>(s, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static int ReadVersion(JsonObject root)
        => root["schemaVersion"] is JsonValue v && v.TryGetValue(out int parsed) ? parsed : 0;

    /// <summary>
    /// Copies the known keys over the retained file. Nested objects are merged rather than replaced,
    /// so an unknown key inside <c>view</c> survives the same way a top-level one does.
    /// </summary>
    private static void Overlay(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject nested && target[key] is JsonObject existing)
            {
                Overlay(existing, nested);
                continue;
            }
            target[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Brings an older file up to the current shape. A file from a <em>later</em> version is left
    /// as it is: its unknown keys are preserved through <see cref="Save"/>, and rewriting the
    /// version number would tell that later version its own migrations had already run.
    /// </summary>
    private static void Migrate(JsonObject root)
    {
        if (ReadVersion(root) >= AppSettings.CurrentSchemaVersion)
            return;

        // Version 0 is a file written before schemaVersion existed; there is nothing in it that has
        // moved, so bringing it forward is only a matter of stamping the version.
        root["schemaVersion"] = AppSettings.CurrentSchemaVersion;
    }

    private void SetAside() => Move(FilePath, FilePath + ".bad");

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is not worth failing over.
        }
    }

    private static void Move(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do — the defaults still apply.
        }
    }
}
