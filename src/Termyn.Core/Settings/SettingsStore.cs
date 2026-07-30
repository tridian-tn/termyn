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
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    /// <summary>The file as last read, so a save can be an overlay rather than a replacement.</summary>
    private JsonObject _raw = new();

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
                {
                    _raw = new JsonObject();
                    return new AppSettings();
                }
                text = File.ReadAllText(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or unreadable: run on defaults for this session and leave the file alone,
                // since it may well be fine and simply busy.
                _raw = new JsonObject();
                return new AppSettings();
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
                _raw = new JsonObject();
                return new AppSettings();
            }

            Migrate(root);
            _raw = root;

            try
            {
                return root.Deserialize<AppSettings>(Format) ?? new AppSettings();
            }
            catch (JsonException)
            {
                // Well-formed JSON with a value of the wrong shape — a string where a number belongs,
                // say. Keep the raw object: the save that follows preserves what we couldn't read.
                return new AppSettings();
            }
        }
    }

    /// <summary>Writes the settings, preserving any keys this version doesn't model.</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            // Never lower the version. A file written by a later Termyn is being saved by an earlier
            // one here, and stamping our own number on it would have that later version re-run
            // migrations over data already in its own shape.
            var version = Math.Max(settings.SchemaVersion, AppSettings.CurrentSchemaVersion);
            var known = JsonSerializer.SerializeToNode(settings with { SchemaVersion = version }, Format) as JsonObject
                        ?? new JsonObject();

            var merged = _raw.DeepClone().AsObject();
            Overlay(merged, known);
            _raw = merged;

            var directory = System.IO.Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Written beside the target and moved into place, so a crash mid-write can't leave a
            // half-written config that the next start would treat as corrupt.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, merged.ToJsonString(Format));
            File.Move(temp, FilePath, overwrite: true);
        }
    }

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
        var version = root["schemaVersion"] is JsonValue v && v.TryGetValue(out int parsed) ? parsed : 0;
        if (version >= AppSettings.CurrentSchemaVersion)
            return;

        // Version 0 is a file written before schemaVersion existed; there is nothing in it that has
        // moved, so bringing it forward is only a matter of stamping the version.
        root["schemaVersion"] = AppSettings.CurrentSchemaVersion;
    }

    private void SetAside()
    {
        try
        {
            File.Move(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do — the defaults still apply, and the save that follows will replace it.
        }
    }
}
