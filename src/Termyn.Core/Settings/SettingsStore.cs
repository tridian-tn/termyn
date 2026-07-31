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
    /// The version the file claimed, kept apart from the settings record: a save must not read it
    /// back off a record that may have been defaulted, or a later version's file gets stamped down.
    /// Null when the file carries a version this build can't read, which is left exactly as it is.
    /// </summary>
    private int? _version = AppSettings.CurrentSchemaVersion;

    /// <summary>False when a file exists but couldn't be read, which makes saving unsafe.</summary>
    private bool _readable = true;

    public SettingsStore(IAppPaths paths)
        : this(System.IO.Path.Combine(paths.ConfigDirectory, "config.json"))
    {
    }

    public SettingsStore(string path) => FilePath = path;

    public string FilePath { get; }

    /// <summary>
    /// Whether a settings file existed the last time <see cref="Load"/> ran. False on a first run,
    /// which is when the app should take its cue from the machine rather than from its own defaults.
    /// </summary>
    public bool Existed { get; private set; }

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
                Existed = File.Exists(FilePath);
                if (!Existed)
                    return Reset();

                text = File.ReadAllText(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or unreadable — busy, most likely. Run on defaults for this session and
                // refuse to save, because an overlay onto nothing would write those defaults over a
                // file whose real contents we never managed to read. Set after Reset, which clears it:
                // the flag means "a file is there and we couldn't read it", nothing else.
                var settings = Reset();
                _readable = false;
                return settings;
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
            _version = HasVersion(root) ? ReadVersion(root) : AppSettings.CurrentSchemaVersion;
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
            // that fell back to defaults carries our version, not the file's. A version we couldn't
            // read is left alone entirely — writing ours over it would tell whichever build put it
            // there that our migrations were its own.
            if (_version is { } version)
                known["schemaVersion"] = Math.Max(version, AppSettings.CurrentSchemaVersion);
            else
                known.Remove("schemaVersion");

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
        _readable = true;
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
            SchemaVersion = ReadVersion(root) ?? defaults.SchemaVersion,
            Hotkey = Text(root, "hotkey", defaults.Hotkey),
            HotkeyEnabled = Flag(root, "hotkeyEnabled", defaults.HotkeyEnabled),
            Theme = Choice(root, "theme", defaults.Theme),
            SyncMode = Choice(root, "syncMode", defaults.SyncMode),
            SyncIntervalSeconds = Int(root, "syncIntervalSeconds", defaults.SyncIntervalSeconds),
            LaunchAtLogin = Flag(root, "launchAtLogin", defaults.LaunchAtLogin),
            CloseToTray = Flag(root, "closeToTray", defaults.CloseToTray),
            View = ReadView(Object(root, "view")),
        };
    }

    private static ViewState ReadView(JsonObject? view)
    {
        var defaults = new ViewState();
        if (view is null)
            return defaults;

        return new ViewState
        {
            SelectedKey = Value(view, "selectedKey") is { } key && key.TryGetValue(out string? s) ? s : defaults.SelectedKey,
            CollapsedKeys = Array(view, "collapsedKeys") is { } keys
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

    /// <summary>
    /// Finds a value by name, ignoring case.
    /// </summary>
    /// <remarks>
    /// Indexing a <see cref="JsonObject"/> is case-sensitive, and the file is documented as
    /// hand-editable — so <c>"Theme"</c> would be silently ignored and then sit in the file next to
    /// the <c>"theme"</c> a save writes, the two contradicting each other.
    /// </remarks>
    private static JsonValue? Value(JsonObject o, string key) => Find(o, key) as JsonValue;

    /// <summary>The nested object by that name, ignoring case.</summary>
    private static JsonObject? Object(JsonObject o, string key)
        => Find(o, key) as JsonObject;

    /// <summary>The array by that name, ignoring case.</summary>
    private static JsonArray? Array(JsonObject o, string key)
        => Find(o, key) as JsonArray;

    /// <summary>Any node by that name, ignoring case.</summary>
    private static JsonNode? Find(JsonObject o, string key)
    {
        if (o[key] is { } exact)
            return exact;

        foreach (var (name, node) in o)
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                return node;

        return null;
    }

    private static string Text(JsonObject o, string key, string fallback)
        => Value(o, key) is { } v && v.TryGetValue(out string? s) && s is not null ? s : fallback;

    private static bool Flag(JsonObject o, string key, bool fallback)
        => Value(o, key) is { } v && v.TryGetValue(out bool b) ? b : fallback;

    private static int Int(JsonObject o, string key, int fallback)
        => Value(o, key) is { } v && v.TryGetValue(out int i) ? i : fallback;

    private static int? Nullable(JsonObject o, string key)
        => Value(o, key) is { } v && v.TryGetValue(out int i) ? i : null;

    /// <summary>
    /// Reads an enum by name. <see cref="Enum.TryParse{T}(string, bool, out T)"/> alone would accept
    /// <c>"3"</c> and hand back a value the enum doesn't have — which the settings dialog can't show
    /// and then fails on, and which is written back as a bare number this reader can't read.
    /// </summary>
    private static T Choice<T>(JsonObject o, string key, T fallback) where T : struct, Enum
        => Value(o, key) is { } v
           && v.TryGetValue(out string? s)
           && Enum.TryParse<T>(s, ignoreCase: true, out var parsed)
           && Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    /// <summary>The version the file claims, or null when it doesn't claim one this version can read.</summary>
    private static int? ReadVersion(JsonObject root)
        => Value(root, "schemaVersion") is { } v && v.TryGetValue(out int parsed) ? parsed : null;

    /// <summary>Whether the file names a version at all, readable or not.</summary>
    private static bool HasVersion(JsonObject root) => Find(root, "schemaVersion") is not null;

    /// <summary>
    /// Copies the known keys over the retained file. Nested objects are merged rather than replaced,
    /// so an unknown key inside <c>view</c> survives the same way a top-level one does.
    /// </summary>
    private static void Overlay(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            // A hand-edited "Theme" was read case-insensitively, so leaving it in place beside the
            // "theme" written here would put two spellings in the file contradicting each other.
            foreach (var variant in target
                         .Where(p => p.Key != key && string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
                         .Select(p => p.Key)
                         .ToList())
            {
                target.Remove(variant);
            }

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
        // A version this build can't read is left exactly as it is. Stamping our own number over a
        // "99.0" or a "99" would tell whichever version wrote it that its migrations had already run
        // — which is the one thing the version marker exists to prevent.
        if (ReadVersion(root) is not { } version)
            return;

        if (version >= AppSettings.CurrentSchemaVersion)
            return;

        // An older version this build does know. There is nothing in the shape it wrote that has
        // moved since, so bringing it forward is only a matter of stamping the version — a real
        // migration would go here, before that line. A file with no version at all took the early
        // return above and is stamped on the way out instead.
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
