using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RobustoClient.ImGui.Theme;

/// <summary>
/// Reads and writes theme palettes as JSON. Every loader merges onto a known-good fallback so a
/// missing or malformed token degrades to a default instead of an unusable UI.
/// </summary>
public static class ThemeJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>Loads a built-in theme from an embedded resource, falling back to <paramref name="fallback"/>.</summary>
    public static RobustaThemePalette LoadEmbedded(string resourceName, RobustaThemePalette fallback)
    {
        using var stream = typeof(ThemeJson).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            MarseyLogger.Warn($"Theme resource '{resourceName}' not found; using built-in fallback.");
            return fallback;
        }

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), fallback);
    }

    /// <summary>Loads a theme from disk, falling back to <paramref name="fallback"/> on any failure.</summary>
    public static RobustaThemePalette LoadFile(string path, RobustaThemePalette fallback)
    {
        try
        {
            if (!File.Exists(path))
                return fallback;

            return Parse(File.ReadAllText(path), fallback);
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"Failed to read theme '{path}': {ex.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// Parses a theme document onto <paramref name="fallback"/>. Unknown tokens are ignored with a
    /// warning; missing tokens keep the fallback value.
    /// </summary>
    public static RobustaThemePalette Parse(string json, RobustaThemePalette fallback)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException ex)
        {
            MarseyLogger.Warn($"Theme JSON is malformed: {ex.Message}");
            return fallback;
        }

        if (root == null)
        {
            MarseyLogger.Warn("Theme JSON root is not an object; keeping current theme.");
            return fallback;
        }

        var name = root["name"]?.GetValue<string>() ?? fallback.Name;

        return new RobustaThemePalette
        {
            Name = name,
            Colors = ReadColors(root["colors"] as JsonObject, fallback.Colors),
            Metrics = ReadSection(root["metrics"] as JsonObject, fallback.Metrics),
            Typography = ReadSection(root["typography"] as JsonObject, fallback.Typography),
            DefaultWindowSize = ReadVector2(root, "defaultWindowSize", fallback.DefaultWindowSize),
            MinWindowSize = ReadVector2(root, "minWindowSize", fallback.MinWindowSize),
            TitleFont = ReadFontKey(root, "titleFont", fallback.TitleFont),
            TextFont = ReadFontKey(root, "textFont", fallback.TextFont)
        };
    }

    /// <summary>
    /// Reads a font key. An unknown key is kept as written rather than rejected here, so a theme can
    /// name a face that a newer build will add; resolution falls back at load time.
    /// </summary>
    private static string ReadFontKey(JsonObject root, string key, string fallback)
    {
        var value = root[key]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>Reads a <c>[width, height]</c> pair, keeping the fallback when absent or malformed.</summary>
    private static Vector2 ReadVector2(JsonObject root, string key, Vector2 fallback)
    {
        if (root[key] is not JsonArray array || array.Count != 2)
            return fallback;

        try
        {
            float w = array[0]!.GetValue<float>();
            float h = array[1]!.GetValue<float>();
            if (w <= 0f || h <= 0f)
                return fallback;

            return new Vector2(w, h);
        }
        catch (Exception)
        {
            MarseyLogger.Warn($"Theme key '{key}' is not a [width, height] pair; keeping default.");
            return fallback;
        }
    }

    /// <summary>Serializes a palette back to the on-disk shape.</summary>
    public static string Write(RobustaThemePalette palette)
    {
        var colors = new JsonObject();
        foreach (var token in ThemeColorTokens.All)
        {
            if (ThemeColorTokens.TryGetProperty(token, out var prop))
                colors[token] = ThemeHex.Format((Vector4)prop.GetValue(palette.Colors)!);
        }

        var root = new JsonObject
        {
            ["name"] = palette.Name,
            ["titleFont"] = palette.TitleFont,
            ["textFont"] = palette.TextFont,
            ["defaultWindowSize"] = new JsonArray(palette.DefaultWindowSize.X, palette.DefaultWindowSize.Y),
            ["minWindowSize"] = new JsonArray(palette.MinWindowSize.X, palette.MinWindowSize.Y),
            ["colors"] = colors,
            ["metrics"] = SerializeSection(palette.Metrics),
            ["typography"] = SerializeSection(palette.Typography)
        };

        return root.ToJsonString(WriteOptions);
    }

    public static void SaveFile(string path, RobustaThemePalette palette)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, Write(palette));
    }

    private static ThemeColors ReadColors(JsonObject? source, ThemeColors fallback)
    {
        // Start from the fallback so a partial file still yields a complete palette.
        var values = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in typeof(ThemeColors).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(Vector4) || prop.GetValue(fallback) is not Vector4 v)
                continue;

            values[char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]] = v;
        }

        if (source != null)
        {
            foreach (var (key, node) in source)
            {
                if (node == null)
                    continue;

                if (!values.ContainsKey(key))
                {
                    MarseyLogger.Warn($"Theme token '{key}' is not recognized; ignoring.");
                    continue;
                }

                string text;
                try
                {
                    text = node.GetValue<string>();
                }
                catch (Exception)
                {
                    MarseyLogger.Warn($"Theme token '{key}' is not a string; keeping default.");
                    continue;
                }

                if (ThemeHex.TryParse(text, out var color))
                    values[key] = color;
                else
                    MarseyLogger.Warn($"Theme token '{key}' has invalid colour '{text}'; keeping default.");
            }
        }

        return BuildColors(values);
    }

    private static ThemeColors BuildColors(Dictionary<string, Vector4> values)
    {
        var built = new ThemeColors();
        foreach (var prop in typeof(ThemeColors).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(Vector4) || !prop.CanWrite)
                continue;

            var key = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            if (values.TryGetValue(key, out var value))
                prop.SetValue(built, value);
        }

        return built;
    }

    /// <summary>
    /// Merges a numeric JSON section onto <paramref name="fallback"/>. Reflection keeps this in sync
    /// automatically when a metric or font size is added.
    /// </summary>
    private static T ReadSection<T>(JsonObject? source, T fallback) where T : class
    {
        if (source == null)
            return fallback;

        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(float))
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, node) in source)
        {
            if (node == null)
                continue;

            if (!props.TryGetValue(key, out var prop))
            {
                MarseyLogger.Warn($"Theme setting '{key}' is not recognized; ignoring.");
                continue;
            }

            try
            {
                prop.SetValue(fallback, node.GetValue<float>());
            }
            catch (Exception)
            {
                MarseyLogger.Warn($"Theme setting '{key}' is not a number; keeping default.");
            }
        }

        return fallback;
    }

    private static JsonObject SerializeSection<T>(T source) where T : class
    {
        var obj = new JsonObject();
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(float))
                continue;

            obj[prop.Name] = JsonValue.Create(MathF.Round((float)prop.GetValue(source)!, 2));
        }

        return obj;
    }
}
