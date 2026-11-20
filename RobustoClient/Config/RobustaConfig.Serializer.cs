using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RobustoClient.UI.ImGui.Radial;

namespace RobustoClient;

public static partial class RobustaConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const BindingFlags ConfigFieldFlags = BindingFlags.Public | BindingFlags.Static;

    private static readonly object ConfigLock = new();
    private static bool _dirty;

    /// <summary>File text as this process last read or wrote it, so an outside edit is adopted.</summary>
    private static string? _lastFileContents;
    internal static string? ConfigPathOverride { get; set; }

    static RobustaConfig() => AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveIfDirty();

    /// <summary>Location of the persisted config, surfaced in logs for troubleshooting.</summary>
    public static string ConfigPath => ConfigPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RobustoClient",
        "Configs",
        "config.json");

    /// <summary>Marks the in-memory config as differing from disk. Does not write.</summary>
    public static void MarkDirty()
    {
        lock (ConfigLock)
            _dirty = true;
    }

    /// <summary>Writes the config to disk if <see cref="MarkDirty"/> was called since the last save.</summary>
    public static void SaveIfDirty()
    {
        lock (ConfigLock)
        {
            if (!_dirty)
                return;

            Save();
        }
    }

    public static void Save()
    {
        lock (ConfigLock)
        {
            try
            {
                // The whole file is rebuilt from memory, so anything edited on disk since the last read
                // or write has to be folded in first.
                AdoptExternalEdits();

                var file = ConfigPath;
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var root = new JsonObject();
                foreach (var field in ConfigFields())
                {
                    // Explicit field type keeps enum/HashSet encoding identical to the previous hand-written DTO.
                    root[field.Name] = JsonSerializer.SerializeToNode(field.GetValue(null), field.FieldType, JsonOptions);
                }

                var text = root.ToJsonString(JsonOptions);
                File.WriteAllText(file, text);
                _lastFileContents = text;
                _dirty = false;
            }
            catch (Exception ex)
            {
                MarseyLogger.Warn($"Failed to save config.json: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Folds in every key whose on-disk value differs from what this process last read or wrote. The
    /// file is rewritten from memory in full, so without this an outside edit is lost at the next save.
    /// A key changed both in game and on disk resolves to the on-disk value.
    /// </summary>
    private static void AdoptExternalEdits()
    {
        var file = ConfigPath;
        if (_lastFileContents == null || !File.Exists(file))
            return;

        string onDisk;
        try
        {
            onDisk = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"Failed to re-read config.json before saving: {ex.Message}");
            return;
        }

        if (onDisk == _lastFileContents)
            return;

        JsonObject? previous;
        JsonObject? changed;
        try
        {
            previous = JsonNode.Parse(_lastFileContents) as JsonObject;
            changed = JsonNode.Parse(onDisk) as JsonObject;
        }
        catch (JsonException ex)
        {
            MarseyLogger.Warn($"config.json has invalid JSON ({ex.Message}), overwriting with in-memory values.");
            return;
        }

        if (changed == null)
        {
            MarseyLogger.Warn("config.json was edited but is not a JSON object, keeping it as written.");
            return;
        }

        var adopted = new List<string>();
        foreach (var field in ConfigFields())
        {
            if (JsonNode.DeepEquals(changed[field.Name], previous?[field.Name]))
                continue;

            if (changed[field.Name] is not { } node)
                continue;

            try
            {
                field.SetValue(null, node.Deserialize(field.FieldType, JsonOptions));
                adopted.Add(field.Name);
            }
            catch (JsonException ex)
            {
                MarseyLogger.Warn($"Ignored invalid value for '{field.Name}' in the edited config.json: {ex.Message}");
            }
        }

        (Esp ??= new()).EnsureDefaults();
        RadialMenuImGui.Ensure();

        if (adopted.Count > 0)
            MarseyLogger.Info($"Adopted externally edited config keys: {string.Join(", ", adopted)}.");
    }

    public static void Load()
    {
        lock (ConfigLock)
        {
            try
            {
                var file = ConfigPath;
                if (!File.Exists(file))
                {
                    // First run: write the compiled-in defaults so there is a file to edit.
                    Save();
                    if (File.Exists(file))
                        MarseyLogger.Info($"Created default config at {file}");
                    return;
                }

                var text = File.ReadAllText(file);
                if (JsonNode.Parse(text) is not JsonObject root)
                    return;

                _lastFileContents = text;

                foreach (var field in ConfigFields())
                {
                    // Missing and explicit-null keys keep the compiled-in default, so old files stay loadable.
                    if (root[field.Name] is not { } node)
                        continue;

                    // Isolate failures so one malformed value cannot discard the rest of the config.
                    try
                    {
                        field.SetValue(null, node.Deserialize(field.FieldType, JsonOptions));
                    }
                    catch (JsonException ex)
                    {
                        MarseyLogger.Warn($"Ignored invalid value for '{field.Name}' in config.json: {ex.Message}");
                    }
                }

                MigrateLegacyKeys(root);
                (Esp ??= new()).EnsureDefaults();
                RadialMenuImGui.Ensure();
            }
            catch (Exception ex)
            {
                MarseyLogger.Warn($"Failed to load config.json: {ex.Message}");
            }
        }
    }

    private static void MigrateLegacyKeys(JsonObject root)
    {
        if (root[nameof(DisableVisualOverlays)] is null && root["OverlaysDisable"] is JsonValue oldOverlay &&
            oldOverlay.TryGetValue<bool>(out var overlaysDisabled))
        {
            DisableVisualOverlays = overlaysDisabled;
        }
    }

    /// <summary>Public static fields of <see cref="RobustaConfig"/>, the whole persisted schema.</summary>
    private static FieldInfo[] ConfigFields() =>
        typeof(RobustaConfig)
            .GetFields(ConfigFieldFlags)
            .Where(f => !f.IsDefined(typeof(JsonIgnoreAttribute), false))
            .ToArray();
}